using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.M3u8Downloader.Configuration;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Builds the ffmpeg and ffprobe command lines for a download.
/// </summary>
/// <remarks>
/// Pure and static throughout, and separated from the process supervision in
/// <see cref="FfmpegDownloader"/> for that reason: what ends up on the command line is the part
/// worth pinning down in tests, and none of it needs a process to decide.
///
/// Arguments are returned as a list rather than a joined string so a URL or filename containing
/// quotes or spaces cannot alter the shape of the command.
/// </remarks>
public static class FfmpegArguments
{
    /// <summary>
    /// Builds the ffmpeg argument list for a download.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <param name="outputPath">The temporary output path.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="bestProgramId">
    /// The HLS program carrying the highest-resolution video, when the source exposes more than
    /// one. Drives stream selection; see the remarks.
    /// </param>
    /// <param name="isHls">
    /// Whether ffmpeg will use its HLS demuxer for this input. HLS-only options must not be sent
    /// otherwise: ffmpeg fails the input outright with "Option not found".
    /// </param>
    /// <param name="hasKnownDuration">
    /// Whether the probe found a duration. Gates the length cap; see the remarks on
    /// <see cref="PluginConfiguration.MaxDurationMinutes"/>.
    /// </param>
    /// <param name="probeSucceeded">
    /// Whether ffprobe described the source at all. Distinguishes "one program, nothing to choose
    /// between" from "we know nothing"; see the remarks on stream selection.
    /// </param>
    /// <param name="reencodeAudio">
    /// Whether to decode and re-encode the audio instead of copying it. The fallback path only;
    /// see <see cref="FfmpegDownloader.IsAudioBitstreamFailure"/> for the failure it exists to recover from.
    /// </param>
    /// <returns>Arguments in order, each already a separate argv entry.</returns>
    /// <remarks>
    /// Pure and static so the argument construction can be asserted in tests without spawning a
    /// process. Arguments are kept as a list rather than a joined string precisely so that a URL
    /// or filename containing quotes or spaces cannot alter the command shape.
    /// </remarks>
    public static IReadOnlyList<string> BuildDownloadArguments(
        string url,
        string outputPath,
        PluginConfiguration config,
        int? bestProgramId = null,
        bool isHls = false,
        bool hasKnownDuration = false,
        bool probeSucceeded = false,
        bool reencodeAudio = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var args = new List<string>
        {
            "-hide_banner",

            // Without this ffmpeg competes with the server for stdin.
            "-nostdin",
            "-loglevel", "error",

            // Machine-readable progress on stdout, leaving stderr purely for diagnostics.
            "-progress", "pipe:1",
        };

        AppendRequestHeaders(args, config);

        // Network robustness. All of these are *input* options and are silently useless after -i,
        // which is why ExtraInputArgs exists separately from ExtraFfmpegArgs.
        //
        // Every one of these defaults to off in ffmpeg, so out of the box a single dropped TLS
        // connection mid-segment ends the whole download. These are http/https protocol options
        // and are safe for any remote input (the queue only accepts http and https URLs).
        args.Add("-reconnect");
        args.Add("1");
        args.Add("-reconnect_streamed");
        args.Add("1");
        args.Add("-reconnect_on_network_error");
        args.Add("1");
        args.Add("-reconnect_delay_max");
        args.Add("30");

        if (config.MaxSpeedMultiplier > 0)
        {
            // Paces reading against the input's own timestamps, so on HLS it throttles how fast
            // segments are fetched. Without it ffmpeg pulls a two-hour stream as fast as the pipe
            // allows -- a dense burst of hundreds of requests in a couple of minutes, which is the
            // shape a rate limiter is built to catch.
            //
            // Invariant formatting matters: a locale that writes "10,5" would be rejected outright.
            args.Add("-readrate");
            args.Add(config.MaxSpeedMultiplier.ToString("0.####", CultureInfo.InvariantCulture));
        }

        if (isHls)
        {
            // HLS-demuxer options. Gated on isHls because ffmpeg rejects the whole input with
            // "Option not found" when the demuxer does not define them.

            // Defaults to 0: without this, one truncated segment aborts the entire download
            // rather than being re-fetched.
            args.Add("-seg_max_retry");
            args.Add("5");

            if (!config.ReuseHttpConnections)
            {
                // The opt-out, for CDNs that rotate the hostname per segment: keep-alive there
                // produces "Cannot reuse HTTP connection for different host", truncated segments,
                // and a failed mux, and a fresh connection per segment avoids that class of
                // failure entirely.
                //
                // It is the opt-out rather than the default because the cost is steep on every
                // other host: a two-hour stream at six-second segments means about 1200 fresh
                // TCP+TLS handshakes, and connection rate is exactly what a limiter counts.
                args.Add("-http_persistent");
                args.Add("0");
            }
        }

        if (config.TolerateCorruptSegments)
        {
            // A host that truncates a segment ("Stream ends prematurely at N, should be M") leaves
            // a partial frame at its end. Without these the demuxer hands that frame on, the
            // Matroska muxer's aac_adtstoasc filter rejects it, and the whole download dies at
            // whatever point the first bad segment landed -- after which retrying just truncates
            // the same segment again.
            //
            // discardcorrupt drops the packets the demuxer already flagged as damaged, so nothing
            // invalid reaches the muxer; ignore_err keeps the demuxer going past the errors that
            // produced them. A stream with no damaged packets is unaffected by either.
            args.Add("-err_detect");
            args.Add("ignore_err");
            args.Add("-fflags");
            args.Add("+discardcorrupt");
        }

        args.AddRange(SplitArguments(config.ExtraInputArgs));

        args.Add("-i");
        args.Add(url);

        // Stream selection. The goal is every real audio track and subtitle language, but only one
        // copy of the video.
        //
        // An HLS master playlist exposes each bitrate rendition as its own video stream while
        // sharing the audio and subtitle tracks, and groups each rendition with those shared
        // tracks into a "program". So mapping the program that holds the best video yields one
        // video plus every audio track and every subtitle -- whereas "-map 0:v -map 0:a -map 0:s?"
        // would copy the same video five times over on such a source.
        //
        // -dn is required here because a program also contains HLS timed-metadata (ID3) streams,
        // which Matroska cannot store: without it, muxing fails outright with "Only audio, video,
        // and subtitles are supported for Matroska".
        if (bestProgramId is not null)
        {
            args.Add("-map");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"0:p:{bestProgramId.Value}"));
            args.Add("-dn");
        }
        else if (isHls && !probeSucceeded)
        {
            // Nothing is mapped at all: ffmpeg's own default selection takes one video (the
            // highest resolution it can see), one audio and one subtitle.
            //
            // This case is a failed probe on an HLS source, where we know nothing about the
            // structure. Falling through to the per-type mapping below would be actively
            // dangerous here: on a master playlist every bitrate rendition is its own video
            // stream, so "0:v?" selects all of them and the HLS demuxer fetches four to six
            // variant playlists *simultaneously* from one host -- which reads as a scraper and
            // gets the server's IP blocked.
            //
            // The cost is the extra audio tracks and subtitles a program map would have kept.
            // That is the right way round: a probe that just failed is precisely when the host is
            // already unhappy with us, and a missing dub track is cheaper than a ban. When the
            // probe succeeds the mapping below is used as before, because a source ffprobe
            // described as having fewer than two programs has no renditions to fan out across.
        }
        else
        {
            // No usable program structure (a plain media playlist, or a non-HLS input): take every
            // video, audio and subtitle stream. Naming the types explicitly cannot select a data
            // stream, so no -dn is needed. The trailing '?' keeps a missing type from being fatal.
            args.Add("-map");
            args.Add("0:v?");
            args.Add("-map");
            args.Add("0:a?");
            args.Add("-map");
            args.Add("0:s?");
        }

        // Remux without re-encoding: fast, lossless, and the whole point of choosing Matroska.
        args.Add("-c");
        args.Add("copy");

        if (reencodeAudio)
        {
            // The fallback after a mux that died inside aac_adtstoasc. Copying AAC into Matroska
            // forces that filter, and it has no tolerance for the half frame a truncated segment
            // ends on: one bad frame kills the whole output. Decoding and re-encoding removes the
            // filter from the path entirely -- the decoder skips what it cannot parse and the
            // encoder emits well-formed frames -- so the download completes with a glitch where
            // each truncated segment landed instead of failing at the first one.
            //
            // Placed after "-c copy" so it overrides only the audio codec: video and subtitles are
            // still copied, and ffmpeg's native AAC encoder needs no external library.
            args.Add("-c:a");
            args.Add("aac");
        }

        // A live stream has no duration and so no end: ffmpeg would keep recording until the disk
        // fills. Placed before the user's own output args so an explicit -t there still wins.
        //
        // Applied only to sources whose length is unknown, because that is the case it exists for:
        // capping a film the probe measured at three hours would silently truncate it and still
        // report the job as completed. LimitLengthOnAllDownloads restores the blanket cap for
        // anyone who wants one.
        if (config.MaxDurationMinutes > 0 && (config.LimitLengthOnAllDownloads || !hasKnownDuration))
        {
            args.Add("-t");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"{config.MaxDurationMinutes * 60}"));
        }

        args.AddRange(SplitArguments(config.ExtraFfmpegArgs));

        args.Add("-f");
        args.Add("matroska");
        args.Add("-y");
        args.Add(outputPath);

        return args;
    }

    /// <summary>
    /// Builds the ffprobe argument list used to inspect a source before downloading it.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <returns>Arguments in order, each already a separate argv entry.</returns>
    /// <remarks>
    /// The probe sends the same request headers as the download. A host that gates on User-Agent or
    /// Referer would otherwise reject the probe while accepting the download, and a failed probe is
    /// not merely cosmetic: without a program to map, stream selection falls back to
    /// <c>-map 0:v?</c>, which copies every bitrate rendition of a master playlist into the output.
    ///
    /// <see cref="PluginConfiguration.ExtraInputArgs"/> is deliberately not forwarded: it holds
    /// ffmpeg input options, and one ffprobe does not define would fail the probe outright -- the
    /// very outcome this method exists to avoid.
    /// </remarks>
    public static IReadOnlyList<string> BuildProbeArguments(string url, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var args = new List<string>
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_programs",
        };

        AppendRequestHeaders(args, config);

        args.Add("-i");
        args.Add(url);

        return args;
    }

    /// <summary>
    /// Splits a user-entered argument string on whitespace, honouring double quotes.
    /// </summary>
    /// <param name="value">The raw setting value.</param>
    /// <returns>Individual arguments.</returns>
    public static IReadOnlyList<string> SplitArguments(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in value)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>
    /// Appends the configured HTTP request headers as ffmpeg/ffprobe input options.
    /// </summary>
    /// <param name="args">The argument list being built.</param>
    /// <param name="config">Current plugin settings.</param>
    private static void AppendRequestHeaders(List<string> args, PluginConfiguration config)
    {
        var userAgent = StripNewlines(config.UserAgent);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            args.Add("-user_agent");
            args.Add(userAgent);
        }

        var referer = StripNewlines(config.Referer);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            args.Add("-headers");
            args.Add("Referer: " + referer + "\r\n");
        }
    }

    /// <summary>
    /// Removes line breaks from a value destined for an HTTP request header.
    /// </summary>
    /// <param name="value">The configured header value.</param>
    /// <returns>The value with any CR or LF removed, and surrounding whitespace trimmed.</returns>
    /// <remarks>
    /// The -headers value is a header block whose entries are separated by CRLF, so a break inside
    /// one entry does not stay inside it. A Referer copied out of a browser's network panel
    /// routinely carries a trailing newline, which is enough to produce a malformed block; a value
    /// containing an embedded one would append whatever followed it as a further header.
    /// </remarks>
    private static string StripNewlines(string? value) =>
        value is null ? string.Empty : value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
}
