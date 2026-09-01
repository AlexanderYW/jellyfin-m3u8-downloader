using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Remuxes an HLS stream to a Matroska file using the server's bundled ffmpeg.
/// </summary>
public sealed partial class FfmpegDownloader : IFfmpegDownloader
{
    /// <summary>How many stderr lines to keep for the failure message.</summary>
    private const int StderrTailLines = 50;

    /// <summary>How many times publishing may re-resolve around a name that appeared mid-download.</summary>
    private const int PublishAttempts = 5;

    /// <summary>Suffix of the in-progress file, before it is published under its real name.</summary>
    /// <remarks>
    /// Public because the worker sweeps abandoned ones at startup; see
    /// <see cref="ReserveOutputPath"/> for why the file is the reservation itself.
    /// </remarks>
    public const string PartExtension = ".part";

    /// <summary>
    /// Serialises output-path selection across concurrent downloads.
    /// </summary>
    /// <remarks>
    /// Resolving a name and reserving it must be one step. Two downloads of the same name would
    /// otherwise both find the path free -- deduplication only consults the filesystem -- and race
    /// to publish it at the end. Held only for the resolve and the reservation, never across the
    /// download itself.
    /// </remarks>
    private static readonly object _reservationLock = new();

    /// <summary>Cap on how long ffprobe may spend measuring the duration.</summary>
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the download is checked for having stalled.
    /// </summary>
    /// <remarks>
    /// The stall window is configured in minutes, so this only has to be fine enough that the kill
    /// lands promptly once it expires.
    /// </remarks>
    private static readonly TimeSpan _stallCheckInterval = TimeSpan.FromSeconds(15);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IDownloadQueueService _queue;
    private readonly ILogger<FfmpegDownloader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegDownloader"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Supplies the ffmpeg and ffprobe paths the server already uses.</param>
    /// <param name="queue">Receives live progress updates.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegDownloader(
        IMediaEncoder mediaEncoder,
        IDownloadQueueService queue,
        ILogger<FfmpegDownloader> logger)
    {
        _mediaEncoder = mediaEncoder;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// Downloads a job to completion.
    /// </summary>
    /// <param name="job">The job to run.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="cancellationToken">Cancels the download and kills ffmpeg.</param>
    /// <returns>The absolute path of the finished file.</returns>
    /// <exception cref="InvalidOperationException">ffmpeg exited non-zero, or stalled.</exception>
    /// <remarks>
    /// The <c>.part</c> file is discarded only while it is still partial. A failure to publish
    /// happens after ffmpeg has succeeded, so the bytes on disk are a complete download and are
    /// left where they are for the admin to rename -- deleting a multi-hour download because the
    /// last <c>File.Move</c> failed would be far worse than the duplicate name a retry then picks.
    /// </remarks>
    public async Task<string> RunAsync(DownloadJob job, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
        {
            throw new InvalidOperationException(
                "The server has not reported an ffmpeg path yet. Check the ffmpeg configuration in Dashboard > Playback.");
        }

        var (finalPath, tempPath) = ReserveOutputPath(config, job.RequestedFileName);

        try
        {
            // Inside the try because the reservation already exists: a cancelled or failed probe
            // must not leave an empty .part file behind holding a name nothing is writing to.
            var source = await ProbeSourceAsync(job.Url, config, cancellationToken).ConfigureAwait(false);
            _queue.ReportProgress(job.Id, 0, source.DurationSeconds);

            await RunFfmpegAsync(job, config, tempPath, source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Only the probe and the download can leave a file that is genuinely partial. Once
            // ffmpeg has exited zero the .part holds a complete download, so publishing is
            // deliberately outside this catch -- see the remarks.
            TryDelete(tempPath);
            throw;
        }

        // Only publish the finished name once ffmpeg has succeeded, so a partial file is never
        // picked up by a library scan.
        return PublishFinishedFile(config, job.RequestedFileName, tempPath, finalPath);
    }

    /// <summary>
    /// Picks the output path and claims it before anything is downloaded.
    /// </summary>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="requestedFileName">The name the job asked for.</param>
    /// <returns>The final path and the temporary path now reserved for it.</returns>
    /// <remarks>
    /// The empty <c>.part</c> file is the reservation itself, and it is created immediately rather
    /// than left to ffmpeg: the probe runs first and may take a minute, which is ample time for a
    /// second download of the same name to pick the same path. Passing a predicate that also sees
    /// <c>.part</c> files is what makes an in-flight download visible to the next one.
    /// </remarks>
    private static (string FinalPath, string TempPath) ReserveOutputPath(PluginConfiguration config, string requestedFileName)
    {
        lock (_reservationLock)
        {
            var finalPath = OutputPathResolver.Resolve(
                config.OutputDirectory,
                requestedFileName,
                path => File.Exists(path) || File.Exists(path + PartExtension));

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            var tempPath = finalPath + PartExtension;

            // ffmpeg is invoked with -y, so the empty placeholder is simply overwritten.
            File.Create(tempPath).Dispose();

            return (finalPath, tempPath);
        }
    }

    /// <summary>
    /// Moves a finished download onto its final name, working around a name taken since it started.
    /// </summary>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="requestedFileName">The name the job asked for.</param>
    /// <param name="tempPath">The completed <c>.part</c> file.</param>
    /// <param name="finalPath">The name reserved when the download began.</param>
    /// <returns>Where the file actually landed.</returns>
    private string PublishFinishedFile(PluginConfiguration config, string requestedFileName, string tempPath, string finalPath)
    {
        string destination;

        // Held across the move as well as the resolve: picking a replacement name and taking it are
        // one step here, exactly as they are when the name is first reserved.
        try
        {
            lock (_reservationLock)
            {
                destination = Publish(
                    config.OutputDirectory,
                    requestedFileName,
                    tempPath,
                    finalPath,
                    File.Exists,
                    (from, to) => File.Move(from, to, overwrite: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The download itself succeeded, so say plainly where the finished bytes are: the job
            // will retry and re-download, but this file can simply be renamed instead.
            _logger.LogError(
                ex,
                "The download finished but could not be published to {Destination}. The complete file is at {TempPath}; rename it by hand to keep it, or it will be swept on the next server restart",
                finalPath,
                tempPath);
            throw;
        }

        if (!string.Equals(destination, finalPath, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "{Reserved} was taken while the download was running; published to {Destination} instead",
                finalPath,
                destination);
        }

        return destination;
    }

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
    /// see <see cref="IsAudioBitstreamFailure"/> for the failure it exists to recover from.
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
    /// Determines whether a configured proxy value is one ffmpeg can actually use.
    /// </summary>
    /// <param name="value">The configured proxy URL.</param>
    /// <returns><c>true</c> when the value is an absolute <c>http</c> URL.</returns>
    /// <remarks>
    /// ffmpeg only speaks to HTTP proxies, and reaches them over plain HTTP even for an
    /// <c>https</c> stream, which it tunnels with <c>CONNECT</c>. A <c>socks5://</c> or
    /// <c>https://</c> value would be accepted silently by the environment and then ignored -- or
    /// worse, misparsed -- so it is rejected here where it can be reported instead.
    /// </remarks>
    public static bool IsUsableProxyUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies the configured proxy to a child process environment.
    /// </summary>
    /// <param name="environment">The child process environment to modify.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <returns>
    /// A redacted description of the proxy that was applied, or <c>null</c> when none was
    /// configured or the configured value was unusable.
    /// </returns>
    /// <remarks>
    /// The proxy travels as an environment variable rather than ffmpeg's <c>-http_proxy</c> input
    /// option deliberately. The option applies to the input ffmpeg opens directly, which leaves
    /// every nested HTTP request an HLS playlist makes -- segments, AES key URIs, variant playlists
    /// -- going out unproxied; the environment variable covers all of them. Both the lowercase and
    /// uppercase spellings are set because different builds and helper tools read different ones.
    ///
    /// Setting this on the child only is the whole point: the rest of the server keeps its own
    /// networking.
    /// </remarks>
    public static string? ApplyProxyEnvironment(IDictionary<string, string?> environment, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(config);

        if (!IsUsableProxyUrl(config.ProxyUrl))
        {
            return null;
        }

        var proxy = config.ProxyUrl.Trim();
        environment["http_proxy"] = proxy;
        environment["https_proxy"] = proxy;
        environment["HTTP_PROXY"] = proxy;
        environment["HTTPS_PROXY"] = proxy;

        if (!string.IsNullOrWhiteSpace(config.ProxyBypassList))
        {
            var bypass = config.ProxyBypassList.Trim();
            environment["no_proxy"] = bypass;
            environment["NO_PROXY"] = bypass;
        }

        return RedactProxy(proxy);
    }

    /// <summary>
    /// Moves a finished download onto its reserved name, resolving a fresh one if it was taken.
    /// </summary>
    /// <param name="outputRoot">The configured output directory.</param>
    /// <param name="requestedFileName">The name the job asked for.</param>
    /// <param name="tempPath">The completed <c>.part</c> file to move.</param>
    /// <param name="finalPath">The name reserved when the download began.</param>
    /// <param name="fileExists">Existence predicate; production passes <see cref="File.Exists(string)"/>.</param>
    /// <param name="move">Performs the move, failing if the destination exists.</param>
    /// <returns>Where the file actually landed.</returns>
    /// <remarks>
    /// The <c>.part</c> reservation makes a collision with another job impossible, but nothing stops
    /// a person or another program from creating the file during what may be a multi-hour download.
    /// Failing there would delete the finished download and start it over, so a fresh name is
    /// resolved and the move retried -- exactly what deduplication would have done had the file
    /// existed when the job started.
    ///
    /// The filesystem is injected for the same reason it is in <see cref="OutputPathResolver"/>: the
    /// interesting behaviour is the retry, and it should be assertable without a real disk.
    ///
    /// An <see cref="IOException"/> that is not a collision -- and one that outlives
    /// <see cref="PublishAttempts"/> tries -- propagates. The caller leaves the finished
    /// <c>.part</c> in place rather than discarding it.
    /// </remarks>
    public static string Publish(
        string outputRoot,
        string requestedFileName,
        string tempPath,
        string finalPath,
        Func<string, bool> fileExists,
        Action<string, string> move)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(move);

        var destination = finalPath;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move(tempPath, destination);
                return destination;
            }
            catch (IOException) when (attempt < PublishAttempts && fileExists(destination))
            {
                destination = OutputPathResolver.Resolve(
                    outputRoot,
                    requestedFileName,
                    // Our own .part is not a collision: it is the file being published.
                    path => (!string.Equals(path + PartExtension, tempPath, StringComparison.Ordinal)
                            && fileExists(path + PartExtension))
                        || fileExists(path));
            }
        }
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
    /// Runs ffmpeg, streaming progress into the queue.
    /// </summary>
    /// <param name="job">The job being run.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="tempPath">Where ffmpeg writes.</param>
    /// <param name="source">What the probe learned about the input.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A task that completes when ffmpeg exits successfully.</returns>
    private async Task RunFfmpegAsync(
        DownloadJob job,
        PluginConfiguration config,
        string tempPath,
        SourceInfo source,
        CancellationToken cancellationToken)
    {
        var (error, audioBitstream) = await RunFfmpegAttemptAsync(
            job, config, tempPath, source, reencodeAudio: false, cancellationToken).ConfigureAwait(false);

        if (audioBitstream)
        {
            // Retrying the copy verbatim would fail at the same frame, so the one retry that has
            // any chance is a different command. See IsAudioBitstreamFailure for why this is the
            // only failure worth spending an extra full download on: everything else either
            // succeeds on a plain retry or would not have been fixed by re-encoding either.
            _logger.LogWarning(
                "Download {JobId} failed while copying its audio into Matroska; retrying once with the audio re-encoded.",
                job.Id);

            (error, _) = await RunFfmpegAttemptAsync(
                job, config, tempPath, source, reencodeAudio: true, cancellationToken).ConfigureAwait(false);
        }

        if (error is not null)
        {
            throw error;
        }
    }

    /// <summary>
    /// Runs ffmpeg once and reports how it ended.
    /// </summary>
    /// <param name="job">The job being downloaded.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="tempPath">The reserved <c>.part</c> path ffmpeg writes to.</param>
    /// <param name="source">What the probe found.</param>
    /// <param name="reencodeAudio">Whether this is the re-encoding fallback attempt.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>
    /// The failure to raise, or <c>null</c> on success, together with whether that failure was the
    /// audio bitstream filter. Returned rather than thrown so the caller can decide to try again;
    /// a cancellation or a stall still throws, because neither is retryable here.
    /// </returns>
    /// <remarks>
    /// Each attempt is a fresh ffmpeg writing the same <c>.part</c> from the start -- the argument
    /// list ends in <c>-y</c>, so the abandoned partial output is overwritten rather than appended
    /// to.
    /// </remarks>
    private async Task<(Exception? Error, bool AudioBitstream)> RunFfmpegAttemptAsync(
        DownloadJob job,
        PluginConfiguration config,
        string tempPath,
        SourceInfo source,
        bool reencodeAudio,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in BuildDownloadArguments(
            job.Url,
            tempPath,
            config,
            source.BestProgramId,
            source.IsHls,
            source.DurationSeconds is > 0,
            source.ProbeSucceeded,
            reencodeAudio))
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyProxy(startInfo, config);

        _logger.LogInformation("Starting m3u8 download {JobId} to {Path}", job.Id, tempPath);
        _logger.LogDebug("ffmpeg {Path} {Args}", startInfo.FileName, string.Join(' ', startInfo.ArgumentList));

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg.");
        }

        var stderrTail = new Queue<string>(StderrTailLines);
        var stallTimeout = TimeSpan.FromMinutes(Math.Max(0, config.StallTimeoutMinutes));
        var stall = new StallDetector(stallTimeout, DateTime.UtcNow);

        // Both pipes must be drained concurrently with the wait: a chatty stream will fill a pipe
        // buffer and deadlock ffmpeg if we only wait for exit. These readers finish on EOF, which
        // happens when the process dies, so they need no cancellation token of their own.
        var stdoutTask = PumpProgressAsync(process.StandardOutput, job.Id, source.DurationSeconds, stall);
        var stderrTask = PumpStderrAsync(process.StandardError, stderrTail);

        bool stalled;

        try
        {
            stalled = await WaitForExitOrStallAsync(process, stall, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        if (stalled)
        {
            TryKill(process);
            await WaitQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            // Deliberately not an OperationCanceledException: the worker reads that as a user
            // cancel and parks the job in Canceled, whereas a stall is a failure and belongs in
            // the ordinary retry budget alongside any other one.
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ffmpeg made no progress for {config.StallTimeoutMinutes} minute(s) and was stopped: {Describe(stderrTail)}"));
        }

        await WaitQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return (null, false);
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"ffmpeg exited with code {process.ExitCode}: {Describe(stderrTail)}");

        // Same message either way; the type is what tells the worker to back off across the
        // whole host instead of retrying this job in a few seconds.
        Exception error = IsRateLimited(stderrTail)
            ? new RateLimitedException(message)
            : new InvalidOperationException(message);

        // A rate limit outranks the bitstream check: re-encoding cannot help a host that is
        // refusing us, and the second attempt would only spend more of the budget it is angry
        // about. The fallback is also pointless on the attempt that already used it.
        return (error, !reencodeAudio && error is not RateLimitedException && IsAudioBitstreamFailure(stderrTail));
    }

    /// <summary>
    /// Waits for ffmpeg to exit, giving up on one that has stopped making progress.
    /// </summary>
    /// <param name="process">The running ffmpeg.</param>
    /// <param name="stall">The detector fed by the progress pump.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// <c>true</c> when the wait was abandoned because the download stalled; <c>false</c> when
    /// ffmpeg exited on its own. Reported rather than thrown so the caller keeps one linear path
    /// for killing the process and draining its pipes.
    /// </returns>
    /// <remarks>
    /// The reconnect options this plugin sets by default mean a host that goes silent is retried
    /// rather than failed, so without this a dead stream holds its queue slot until an admin
    /// cancels it by hand -- and at the default concurrency of one, that is the whole queue.
    /// </remarks>
    private static async Task<bool> WaitForExitOrStallAsync(Process process, StallDetector stall, CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(cancellationToken);

        if (!stall.IsEnabled)
        {
            await exitTask.ConfigureAwait(false);
            return false;
        }

        while (true)
        {
            var tick = Task.Delay(_stallCheckInterval, cancellationToken);
            var finished = await Task.WhenAny(exitTask, tick).ConfigureAwait(false);

            if (ReferenceEquals(finished, exitTask))
            {
                // Awaited rather than just returned so a cancellation still surfaces here.
                await exitTask.ConfigureAwait(false);
                return false;
            }

            // Observe the timer so a cancellation during the wait is not left unobserved.
            await tick.ConfigureAwait(false);

            if (stall.IsStalled(DateTime.UtcNow))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Renders the retained stderr tail for a failure message.
    /// </summary>
    /// <param name="stderrTail">The recent stderr lines.</param>
    /// <returns>The diagnostic text.</returns>
    private static string Describe(Queue<string> stderrTail) => stderrTail.Count > 0
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

    /// <summary>
    /// Reads ffmpeg's progress stream and pushes positions into the queue.
    /// </summary>
    /// <param name="reader">ffmpeg's stdout.</param>
    /// <param name="jobId">The job being reported on.</param>
    /// <param name="duration">The probed duration, if any.</param>
    /// <param name="stall">Told about each position so it can tell a slow download from a dead one.</param>
    /// <returns>A task that completes at end of stream.</returns>
    private async Task PumpProgressAsync(StreamReader reader, Guid jobId, double? duration, StallDetector stall)
    {
        var position = 0d;

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var sample = ParseProgressLine(line);
            if (!sample.HasValue)
            {
                continue;
            }

            // Position and speed arrive on separate lines. Carrying the last position forward is
            // what lets a speed-only line be reported without rewinding the progress bar.
            position = sample.PositionSeconds ?? position;
            _queue.ReportProgress(jobId, position, duration, sample.SpeedRatio);

            // Only an advancing position counts as a sign of life; the detector ignores the rest.
            stall.Observe(position, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Reads ffmpeg's stderr, retaining only the tail for diagnostics.
    /// </summary>
    /// <param name="reader">ffmpeg's stderr.</param>
    /// <param name="tail">Bounded buffer of recent lines.</param>
    /// <returns>A task that completes at end of stream.</returns>
    private async Task PumpStderrAsync(StreamReader reader, Queue<string> tail)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Bounded so a stream that errors on every segment cannot grow this without limit.
            if (tail.Count == StderrTailLines)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
            _logger.LogDebug("ffmpeg: {Line}", line);
        }
    }

    /// <summary>
    /// Asks ffprobe what it can about the source before the download starts.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>
    /// The duration and best program, either of which may be <c>null</c>. A failed probe is never
    /// fatal: without a duration the progress readout falls back to elapsed time, and without a
    /// program the download falls back to explicit per-type stream mapping.
    /// </returns>
    private async Task<SourceInfo> ProbeSourceAsync(string url, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.ProbePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in BuildProbeArguments(url, config))
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyProxy(startInfo, config);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_probeTimeout);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new SourceInfo(null, null, LooksLikeHlsUrl(url));
            }

            var readTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

            // Drained even though "-v quiet" should keep it empty: an unread pipe that does fill up
            // blocks ffprobe until the timeout, turning a fast probe into a 60-second stall.
            var drainErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var json = await readTask.ConfigureAwait(false);
            await drainErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return new SourceInfo(null, null, LooksLikeHlsUrl(url));
            }

            using var document = JsonDocument.Parse(json);
            return new SourceInfo(
                ReadDuration(document.RootElement),
                ReadBestProgramId(document.RootElement),
                ReadIsHls(document.RootElement) || LooksLikeHlsUrl(url),
                ProbeSucceeded: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled the job; let the caller handle it.
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Could not probe {Url}", url);
        }

        return new SourceInfo(null, null, LooksLikeHlsUrl(url));
    }

    /// <summary>
    /// Appends the configured HTTP request headers as ffmpeg/ffprobe input options.
    /// </summary>
    /// <param name="args">The argument list being built.</param>
    /// <param name="config">Current plugin settings.</param>
    private static void AppendRequestHeaders(List<string> args, PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.UserAgent))
        {
            args.Add("-user_agent");
            args.Add(config.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(config.Referer))
        {
            args.Add("-headers");
            args.Add("Referer: " + config.Referer + "\r\n");
        }
    }

    /// <summary>
    /// Points a child process at the configured proxy, reporting a value that cannot be used.
    /// </summary>
    /// <param name="startInfo">The child process about to be started.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <remarks>
    /// Reading <see cref="ProcessStartInfo.Environment"/> seeds it from this process, so the child
    /// still inherits everything the server was started with; only the proxy variables are added.
    ///
    /// A proxy that cannot be parsed is reported and skipped rather than thrown: a typo in a
    /// settings field should not turn every queued download into a failure, and a silent fallback
    /// to a direct connection is exactly the surprise a proxy user does not want.
    /// </remarks>
    private void ApplyProxy(ProcessStartInfo startInfo, PluginConfiguration config)
    {
        var proxy = ApplyProxyEnvironment(startInfo.Environment, config);

        if (proxy is not null)
        {
            _logger.LogDebug("Routing through HTTP proxy {Proxy}", proxy);
        }
        else if (!string.IsNullOrWhiteSpace(config.ProxyUrl))
        {
            _logger.LogWarning(
                "Ignoring the configured proxy: it must be an absolute http:// URL, and ffmpeg does not support SOCKS proxies.");
        }
    }

    /// <summary>
    /// Strips any credentials from a proxy URL so it can be logged.
    /// </summary>
    /// <param name="proxy">The proxy URL.</param>
    /// <returns>The URL with any <c>user:pass@</c> replaced by <c>***@</c>.</returns>
    private static string RedactProxy(string proxy)
    {
        var schemeEnd = proxy.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return proxy;
        }

        // Only the userinfo section can carry a password, and it ends at the first '@' of the
        // authority -- which is also the last one, since '@' is not legal in a host.
        var authority = schemeEnd + 3;
        var at = proxy.IndexOf('@', authority);
        return at < 0
            ? proxy
            : string.Concat(proxy.AsSpan(0, authority), "***", proxy.AsSpan(at));
    }

    /// <summary>
    /// Determines whether ffprobe demuxed the input as HLS.
    /// </summary>
    /// <param name="root">The ffprobe JSON root.</param>
    /// <returns><c>true</c> when the HLS demuxer handled the input.</returns>
    private static bool ReadIsHls(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format)
            || !format.TryGetProperty("format_name", out var nameElement))
        {
            return false;
        }

        // format_name is a comma-separated list of candidate demuxers, e.g. "hls" or
        // "hls,applehttp" depending on the build.
        var name = nameElement.GetString();
        return name is not null
            && name.Split(',').Any(n => n.Trim().Equals("hls", StringComparison.OrdinalIgnoreCase)
                || n.Trim().Equals("applehttp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Falls back to the URL when the probe could not identify the demuxer.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <returns><c>true</c> when the URL path names a playlist.</returns>
    /// <remarks>
    /// A probe can fail on a source that still downloads fine (a host that rejects ffprobe's
    /// request but not ffmpeg's, for instance). Losing the HLS tuning options in that case would
    /// mean losing exactly the robustness that a flaky host calls for.
    /// </remarks>
    private static bool LooksLikeHlsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the total duration out of an ffprobe document.
    /// </summary>
    /// <param name="root">The ffprobe JSON root.</param>
    /// <returns>The duration in seconds, or <c>null</c> when absent (e.g. a live stream).</returns>
    private static double? ReadDuration(JsonElement root)
    {
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationElement)
            && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return seconds;
        }

        return null;
    }

    /// <summary>
    /// Finds the program carrying the highest-resolution video.
    /// </summary>
    /// <param name="root">The ffprobe JSON root.</param>
    /// <returns>
    /// The program id to map, or <c>null</c> when the source has fewer than two programs — with a
    /// single program there is nothing to choose between, and plain per-type mapping is simpler
    /// and less likely to surprise.
    /// </returns>
    private static int? ReadBestProgramId(JsonElement root)
    {
        if (!root.TryGetProperty("programs", out var programs) || programs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (programs.GetArrayLength() < 2)
        {
            return null;
        }

        int? bestId = null;
        long bestPixels = -1;

        foreach (var program in programs.EnumerateArray())
        {
            if (!program.TryGetProperty("program_id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                continue;
            }

            if (!program.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type)
                    || !string.Equals(type.GetString(), "video", StringComparison.Ordinal))
                {
                    continue;
                }

                var pixels = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var width)
                    && stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var height)
                        ? (long)width * height
                        : 0;

                if (pixels > bestPixels)
                {
                    bestPixels = pixels;
                    bestId = id;
                }
            }
        }

        return bestId;
    }

    /// <summary>
    /// Awaits the pipe readers, ignoring failures caused by an already-dead process.
    /// </summary>
    /// <param name="stdoutTask">The stdout pump.</param>
    /// <param name="stderrTask">The stderr pump.</param>
    /// <returns>A task that always completes successfully.</returns>
    private async Task WaitQuietlyAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "ffmpeg output stream ended abruptly");
        }
    }

    /// <summary>
    /// Kills the ffmpeg process tree, tolerating a race with normal exit.
    /// </summary>
    /// <param name="process">The process to kill.</param>
    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            _logger.LogDebug(ex, "Could not kill the ffmpeg process; it likely already exited");
        }
    }

    /// <summary>
    /// Deletes a partial file, ignoring failures.
    /// </summary>
    /// <param name="path">The file to remove.</param>
    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the partial download at {Path}", path);
        }
    }
}
