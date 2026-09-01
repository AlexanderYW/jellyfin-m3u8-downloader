using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Reads meaning out of what ffmpeg writes to its progress and error streams.
/// </summary>
/// <remarks>
/// ffmpeg reports through text and nothing else -- there is no status code or structured channel
/// to consult -- so every judgement about how a download is going, or why it stopped, comes down
/// to parsing. Keeping that here, pure and static and away from the process supervision in
/// <see cref="FfmpegDownloader"/>, is what lets each classification be asserted against real
/// ffmpeg output without spawning anything.
/// </remarks>
public static partial class FfmpegOutputClassifier
{
    /// <summary>
    /// Parses one <c>-progress</c> output line.
    /// </summary>
    /// <param name="line">A single <c>key=value</c> line from ffmpeg's progress stream.</param>
    /// <returns>
    /// What the line carried. ffmpeg writes one key per line, so a sample holds either a position
    /// or a speed, never both; a line carrying neither yields an empty sample.
    /// </returns>
    public static ProgressSample ParseProgressLine(string? line)
    {
        var empty = new ProgressSample(null, null);

        if (string.IsNullOrEmpty(line))
        {
            return empty;
        }

        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return empty;
        }

        var key = line.AsSpan(0, separator).Trim();
        var rawValue = line.AsSpan(separator + 1).Trim();

        // "N/A" shows up before the first frame is muxed.
        if (rawValue.IsEmpty || rawValue.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return empty;
        }

        if (key.Equals("out_time_us", StringComparison.Ordinal) || key.Equals("out_time_ms", StringComparison.Ordinal))
        {
            // Both keys are microseconds; out_time_ms is a long-standing ffmpeg misnomer.
            return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)
                ? new ProgressSample(micros / 1_000_000d, null)
                : empty;
        }

        if (key.Equals("out_time", StringComparison.Ordinal))
        {
            return TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out var ts)
                ? new ProgressSample(ts.TotalSeconds, null)
                : empty;
        }

        if (key.Equals("speed", StringComparison.Ordinal))
        {
            // Reported as "1.02x", occasionally padded ("  0.5x"). The trailing unit is always
            // there, so strip it rather than trusting the parser to stop at it.
            var number = rawValue.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                ? rawValue[..^1].Trim()
                : rawValue;

            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0
                ? new ProgressSample(null, speed)
                : empty;
        }

        return empty;
    }

    /// <summary>
    /// Renders the retained stderr tail for a failure message.
    /// </summary>
    /// <param name="stderrTail">The recent stderr lines.</param>
    /// <returns>The diagnostic text.</returns>
    public static string Describe(Queue<string> stderrTail) => stderrTail.Count > 0
        ? string.Join(Environment.NewLine, stderrTail)
        : "no diagnostic output";

    /// <summary>
    /// Decides whether a failure was the host refusing us rather than anything about the stream.
    /// </summary>
    /// <param name="stderr">The tail of ffmpeg's stderr.</param>
    /// <returns><c>true</c> when the output names a rate-limit or forbidden response.</returns>
    /// <remarks>
    /// Public and static for the same reason as <see cref="ParseProgressLine"/>: the interesting
    /// behaviour is the classification, and it should be assertable without spawning a process.
    ///
    /// Matching on ffmpeg's text is unavoidable -- it surfaces no status code through any other
    /// channel -- so this errs towards the phrasings ffmpeg actually emits
    /// ("Server returned 403 Forbidden", "HTTP error 429 Too Many Requests") rather than trying to
    /// catch every 4xx. A false negative simply falls back to the ordinary retry, which is what
    /// happened before this existed; a false positive would park a job for the whole backoff over
    /// something unrelated, which is the worse mistake.
    /// </remarks>
    public static bool IsRateLimited(IEnumerable<string> stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        foreach (var line in stderr)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (RateLimitPattern().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches the ways ffmpeg names a host refusing us.
    /// </summary>
    /// <returns>The compiled pattern.</returns>
    /// <remarks>
    /// The status code must appear in an HTTP context -- "HTTP error 403", "Server returned 429",
    /// or the code immediately followed by its reason phrase. A bare three-digit run is
    /// deliberately not enough: ffmpeg puts segment numbers and URLs in its errors, so
    /// "Failed to open segment 1403 of playlist 0" and "Opening 'https://cdn/seg_4290.ts'" both
    /// contain one, and treating either as a refusal would park every job on the host for the whole
    /// backoff over an ordinary segment error.
    ///
    /// "Too Many Requests" stands alone because the phrase has no other meaning. "Forbidden" does
    /// not: it has to be adjacent to a 403 to count.
    /// </remarks>
    [GeneratedRegex(
        @"(?:HTTP\s+error|Server\s+returned(?:\s+code)?)\s+(?:403|429)\b|\b403\s+Forbidden\b|Too\s+Many\s+Requests",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RateLimitPattern();

    /// <summary>
    /// Decides whether a failure was the audio bitstream filter choking on a damaged frame.
    /// </summary>
    /// <param name="stderr">The tail of ffmpeg's stderr.</param>
    /// <returns><c>true</c> when the mux died inside <c>aac_adtstoasc</c>.</returns>
    /// <remarks>
    /// This is the one failure mode that neither retrying nor
    /// <see cref="PluginConfiguration.TolerateCorruptSegments"/> reliably clears. A host that
    /// truncates a segment leaves a partial ADTS frame at its end; if the demuxer does not flag
    /// the packets as corrupt -- and on a segment cut at a TLS record boundary it often does not,
    /// because the bytes that arrived are structurally valid -- <c>+discardcorrupt</c> has nothing
    /// to drop, and the frame reaches the <c>aac_adtstoasc</c> filter that copying AAC into
    /// Matroska requires. That filter fails the whole mux on it, at exactly the same point on
    /// every retry.
    ///
    /// Matched narrowly on the filter's own name together with the muxer's complaint, so an
    /// unrelated "Invalid data found" cannot trigger a pointless re-encode. Anything not matched
    /// here just takes the ordinary retry, which is the behaviour that predates this.
    /// </remarks>
    public static bool IsAudioBitstreamFailure(IEnumerable<string> stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        var sawFilter = false;
        var sawMuxFailure = false;

        foreach (var line in stderr)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.Contains("aac_adtstoasc", StringComparison.OrdinalIgnoreCase))
            {
                sawFilter = true;
            }

            if (line.Contains("Error applying bitstream filters", StringComparison.OrdinalIgnoreCase))
            {
                sawMuxFailure = true;
            }
        }

        return sawFilter && sawMuxFailure;
    }
}
