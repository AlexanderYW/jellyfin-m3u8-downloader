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
public sealed class FfmpegDownloader : IFfmpegDownloader
{
    /// <summary>How many stderr lines to keep for the failure message.</summary>
    private const int StderrTailLines = 50;

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
    private readonly OutputFilePublisher _publisher;
    private readonly ILogger<FfmpegDownloader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegDownloader"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Supplies the ffmpeg and ffprobe paths the server already uses.</param>
    /// <param name="queue">Receives live progress updates.</param>
    /// <param name="publisher">Reserves the output name and publishes the finished file.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegDownloader(
        IMediaEncoder mediaEncoder,
        IDownloadQueueService queue,
        OutputFilePublisher publisher,
        ILogger<FfmpegDownloader> logger)
    {
        _mediaEncoder = mediaEncoder;
        _queue = queue;
        _publisher = publisher;
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

        var (finalPath, tempPath) = _publisher.ReserveOutputPath(config.OutputDirectory, job.RequestedFileName);

        try
        {
            // Inside the try because the reservation already exists: a cancelled or failed probe
            // must not leave an empty .part file behind holding a name nothing is writing to.
            var source = CachedProbe(job);

            if (source is null)
            {
                source = await ProbeSourceAsync(job.Url, config, cancellationToken).ConfigureAwait(false);

                if (source.ProbeSucceeded)
                {
                    // Kept for any automatic retry of this job. A remux has no resume point, so a
                    // retry re-requests the whole stream; not spending an extra probe request on
                    // top of that matters most when the retry follows a rate limit.
                    _queue.RecordProbe(job.Id, source.DurationSeconds, source.BestProgramId, source.IsHls);
                }
            }

            _queue.ReportProgress(job.Id, 0, source.DurationSeconds);

            await RunFfmpegAsync(job, config, tempPath, source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Only the probe and the download can leave a file that is genuinely partial. Once
            // ffmpeg has exited zero the .part holds a complete download, so publishing is
            // deliberately outside this catch -- see the remarks.
            _publisher.TryDelete(tempPath);
            throw;
        }

        // Only publish the finished name once ffmpeg has succeeded, so a partial file is never
        // picked up by a library scan.
        return _publisher.PublishFinishedFile(config.OutputDirectory, job.RequestedFileName, tempPath, finalPath);
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

        foreach (var arg in FfmpegArguments.BuildDownloadArguments(
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
                    $"ffmpeg made no progress for {config.StallTimeoutMinutes} minute(s) and was stopped: {FfmpegOutputClassifier.Describe(stderrTail)}"));
        }

        await WaitQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return (null, false);
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"ffmpeg exited with code {process.ExitCode}: {FfmpegOutputClassifier.Describe(stderrTail)}");

        // Same message either way; the type is what tells the worker to back off across the
        // whole host instead of retrying this job in a few seconds.
        Exception error = FfmpegOutputClassifier.IsRateLimited(stderrTail)
            ? new RateLimitedException(message)
            : new InvalidOperationException(message);

        // A rate limit outranks the bitstream check: re-encoding cannot help a host that is
        // refusing us, and the second attempt would only spend more of the budget it is angry
        // about. The fallback is also pointless on the attempt that already used it.
        return (error, !reencodeAudio && error is not RateLimitedException && FfmpegOutputClassifier.IsAudioBitstreamFailure(stderrTail));
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
            var sample = FfmpegOutputClassifier.ParseProgressLine(line);
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
    /// Returns what an earlier attempt learned about this job's source, if anything.
    /// </summary>
    /// <param name="job">The job being run.</param>
    /// <returns>The cached probe result, or <c>null</c> when the source has not been described yet.</returns>
    /// <remarks>
    /// Only a successful probe is ever recorded, so a cached result always stands for one --
    /// which is what lets it be handed back with <see cref="SourceInfo.ProbeSucceeded"/> set.
    /// </remarks>
    private static SourceInfo? CachedProbe(DownloadJob job) =>
        job.ProbedUtc is null
            ? null
            : new SourceInfo(job.DurationSeconds, job.ProbedProgramId, job.ProbedIsHls ?? false, ProbeSucceeded: true);

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

        foreach (var arg in FfmpegArguments.BuildProbeArguments(url, config))
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
        var proxy = ProxySettings.ApplyProxyEnvironment(startInfo.Environment, config);

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
}
