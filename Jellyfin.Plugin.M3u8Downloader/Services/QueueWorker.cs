using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Drains the download queue one job at a time for the lifetime of the server.
/// </summary>
public sealed class QueueWorker : BackgroundService
{
    /// <summary>Floor on the configured concurrency; zero would stall the queue outright.</summary>
    private const int MinConcurrency = 1;

    /// <summary>
    /// Ceiling on the configured concurrency. Each slot is an ffmpeg process writing to the same
    /// disk, so past a handful they contend rather than go faster.
    /// </summary>
    private const int MaxConcurrency = 8;

    /// <summary>
    /// Longest the worker sleeps when idle. Bounded rather than infinite so a job re-queued with
    /// a retry delay is picked up even though nothing signals the queue when its backoff expires.
    /// </summary>
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often history is pruned.
    /// </summary>
    /// <remarks>
    /// On its own schedule rather than only when the queue drains: a server that always has
    /// something downloading would otherwise never prune at all.
    /// </remarks>
    private static readonly TimeSpan _pruneInterval = TimeSpan.FromMinutes(5);

    private readonly IDownloadQueueService _queue;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly IFfmpegDownloader _downloader;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<QueueWorker> _logger;

    /// <summary>Guards the "waiting for ffmpeg" message so it is logged once, not every tick.</summary>
    private bool _loggedEncoderWait;

    /// <summary>Guards the "queue is paused" message so it is logged on the transition only.</summary>
    private bool _loggedPaused;

    /// <summary>When history was last pruned.</summary>
    private DateTime _lastPruneUtc = DateTime.MinValue;

    /// <summary>Whether the startup sweep for abandoned <c>.part</c> files has run.</summary>
    private bool _sweptPartFiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueWorker"/> class.
    /// </summary>
    /// <param name="queue">The download queue.</param>
    /// <param name="configuration">Reads the current plugin settings on each use.</param>
    /// <param name="downloader">Runs the actual ffmpeg download.</param>
    /// <param name="libraryMonitor">Notified about finished files so Jellyfin picks them up.</param>
    /// <param name="mediaEncoder">Used to tell whether the server knows where ffmpeg is yet.</param>
    /// <param name="logger">The logger.</param>
    public QueueWorker(
        IDownloadQueueService queue,
        Func<PluginConfiguration> configuration,
        IFfmpegDownloader downloader,
        ILibraryMonitor libraryMonitor,
        IMediaEncoder mediaEncoder,
        ILogger<QueueWorker> logger)
    {
        _queue = queue;
        _configuration = configuration;
        _downloader = downloader;
        _libraryMonitor = libraryMonitor;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("M3U8 download queue worker started");

        var running = new List<Task>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // This service starts before the server has finished initialising its media
                // encoder, so early on EncoderPath is still empty. Claiming a job then would fail
                // it with "Cannot start process because a file name has not been provided" and
                // burn a retry for a reason that has nothing to do with the download -- which is
                // exactly what happens to jobs restored from disk at boot. Wait instead.
                if (string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
                {
                    if (!_loggedEncoderWait)
                    {
                        _loggedEncoderWait = true;
                        _logger.LogInformation("Waiting for the server to report its ffmpeg path before starting downloads");
                    }

                    await Task.Delay(_idleTimeout, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var config = _configuration();
                var limit = Math.Clamp(config.MaxConcurrentDownloads, MinConcurrency, MaxConcurrency);

                running.RemoveAll(t => t.IsCompleted);

                // Once, while nothing of ours is in flight: every leftover .part under the output
                // directory is then the debris of a server that was killed rather than shut down.
                // Deferred rather than skipped when no output directory is set yet, so an install
                // configured after startup is still cleaned up -- and gated on an idle worker,
                // because by then a download of our own could be holding a .part of its own.
                if (!_sweptPartFiles && running.Count == 0 && !string.IsNullOrWhiteSpace(config.OutputDirectory))
                {
                    _sweptPartFiles = true;

                    // Off the loop thread: pointed at a large media tree the recursive enumeration
                    // is a multi-second stall, and it would sit between startup and the first job
                    // ever being claimed.
                    await Task.Run(() => SweepAbandonedParts(config), stoppingToken).ConfigureAwait(false);
                }

                Prune(config);

                if (config.QueuePaused)
                {
                    if (!_loggedPaused)
                    {
                        _loggedPaused = true;
                        _logger.LogInformation("The M3U8 download queue is paused; no new downloads will start");
                    }

                    await WaitAsync(running, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _loggedPaused = false;

                if (running.Count < limit
                    && _queue.TryDequeueNext(limit, Math.Max(1, config.MaxConcurrentPerHost)) is { } job)
                {
                    // Started without awaiting: that is what makes the downloads concurrent. Every
                    // outcome is turned into queue state inside ProcessJobAsync, which is why the
                    // task can be parked in a list and never inspected for a result.
                    running.Add(ProcessJobAsync(job, config, stoppingToken));
                    continue;
                }

                await WaitAsync(running, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The loop should never escape on its own; if it somehow does, log it loudly rather
            // than letting the host tear down over a background task.
            _logger.LogError(ex, "The M3U8 download queue worker stopped unexpectedly");
        }

        // Let the in-flight jobs observe the shutdown and record their own state before the host
        // tears the process down; each one already handles cancellation, so this cannot throw.
        await Task.WhenAll(running).ConfigureAwait(false);

        _logger.LogInformation("M3U8 download queue worker stopped");
    }

    /// <summary>
    /// Parks the loop until something is worth looking at again.
    /// </summary>
    /// <param name="running">The downloads currently in flight.</param>
    /// <param name="stoppingToken">The host shutdown token.</param>
    /// <returns>A task that completes when the loop should take another pass.</returns>
    /// <remarks>
    /// Every wait here is bounded rather than indefinite, so a retry whose backoff expires -- which
    /// nothing signals -- is still picked up.
    /// </remarks>
    private async Task WaitAsync(List<Task> running, CancellationToken stoppingToken)
    {
        if (running.Count == 0)
        {
            await _queue.WaitForWorkAsync(_idleTimeout, stoppingToken).ConfigureAwait(false);
            return;
        }

        // At capacity, paused, or nothing is claimable: wake on the first job to finish.
        await Task.WhenAny(Task.WhenAny(running), Task.Delay(_idleTimeout, stoppingToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drops history older than the retention window, at most once per interval.
    /// </summary>
    /// <param name="config">Current plugin settings.</param>
    private void Prune(PluginConfiguration config)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPruneUtc < _pruneInterval)
        {
            return;
        }

        _lastPruneUtc = now;
        _queue.PruneHistory(config.HistoryRetentionDays);
    }

    /// <summary>
    /// Deletes <c>.part</c> files left behind by a server that was killed mid-download.
    /// </summary>
    /// <param name="config">Current plugin settings.</param>
    /// <remarks>
    /// A <c>.part</c> file is not just debris, it is the name reservation: the output path resolver
    /// treats a name with one next to it as taken. One left behind by a hard kill therefore pushes
    /// every later download of that title to "Title (2).mkv" forever. A normal failure or shutdown
    /// cleans up after itself, so anything found at this point is genuinely abandoned.
    /// </remarks>
    private void SweepAbandonedParts(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.OutputDirectory) || !Directory.Exists(config.OutputDirectory))
        {
            return;
        }

        var removed = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                config.OutputDirectory,
                "*" + OutputPathResolver.Extension + OutputFilePublisher.PartExtension,
                SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove the abandoned partial download at {Path}", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not scan {Path} for abandoned partial downloads", config.OutputDirectory);
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} partial download(s) left by an unclean shutdown", removed);
        }
    }

    /// <summary>
    /// Runs a single job to a terminal state, translating every outcome into queue state.
    /// </summary>
    /// <param name="job">The claimed job.</param>
    /// <param name="config">The settings snapshot the job was claimed under.</param>
    /// <param name="stoppingToken">The host shutdown token.</param>
    /// <returns>
    /// A task that completes when the job reaches a terminal or re-queued state. It never faults:
    /// every outcome is recorded as queue state, which is what lets the caller park it in a list
    /// and await the whole set at shutdown.
    /// </returns>
    private async Task ProcessJobAsync(DownloadJob job, PluginConfiguration config, CancellationToken stoppingToken)
    {
        // Linked so both a user cancel and a server shutdown stop ffmpeg, while letting us tell
        // the two apart afterwards.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _queue.SetActiveCancellation(job.Id, jobCts);

        try
        {
            var path = await _downloader.RunAsync(job, config, jobCts.Token).ConfigureAwait(false);

            _queue.MarkCompleted(job.Id, path);
            _logger.LogInformation("Finished m3u8 download {JobId} -> {Path}", job.Id, path);

            NotifyLibrary(config, path);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Server shutdown, not a user decision, so it must not burn a retry. The persisted
            // state still says Downloading, and the queue resets that to Queued when it reloads
            // on the next boot -- so the correct action here is to leave it exactly as it is.
            //
            // This deliberately catches every exception, not just OperationCanceledException:
            // when the host is killed, the OS tears down the whole process tree, so ffmpeg
            // usually dies of its own accord and WaitForExitAsync returns a non-zero exit code
            // before the shutdown token is ever observed. Treating that as a download failure
            // would burn a retry on every restart.
            _logger.LogInformation(
                "M3u8 download {JobId} interrupted by server shutdown; it will restart on the next boot",
                job.Id);
        }
        catch (OperationCanceledException)
        {
            _queue.MarkCanceled(job.Id);
            _logger.LogInformation("Cancelled m3u8 download {JobId}", job.Id);
        }
        catch (ArgumentException ex)
        {
            // A rejected filename or an unconfigured output directory fails identically every
            // time, so retrying only delays the report. Passing maxRetries: 0 sends it straight to
            // Failed through the same accounting as any other permanent failure.
            _queue.MarkAttemptFailed(job.Id, ArgumentErrorText.Describe(ex), 0, TimeSpan.Zero);
            _logger.LogError(ex, "M3u8 download {JobId} cannot be run as configured; not retrying", job.Id);
        }
        catch (RateLimitedException ex)
        {
            // The host is refusing us, not failing on this particular stream. Retrying in the
            // usual few seconds would re-request the whole thing from the first segment -- a remux
            // has no resume point -- while the host is still angry, which is how a soft throttle
            // turns into a lasting block. Back the whole host off instead, so the other episodes
            // queued behind this one wait too.
            // Escalated by attempt and jittered: a host that refuses us three times running should
            // not be probed on the same schedule that already failed twice, and the cooldown is
            // per host, so without the spread every job queued behind this one becomes runnable at
            // the same instant and arrives as the very burst that tripped the limiter.
            var backoff = RetryBackoff.ForRateLimit(
                TimeSpan.FromMinutes(Math.Max(0, config.RateLimitBackoffMinutes)),
                job.Attempts + 1,
                RetryBackoff.DefaultJitterFraction,
                Random.Shared);

            _queue.CoolDownHost(job.Url, backoff);

            var requeued = _queue.MarkAttemptFailed(
                job.Id,
                ex.Message,
                Math.Max(0, config.MaxRetries),
                backoff);

            _logger.LogWarning(
                ex,
                "M3u8 download {JobId} was rate-limited by {Host}; pausing downloads from that host for {Minutes:F1} minute(s)",
                job.Id,
                HostOf(job.Url),
                backoff.TotalMinutes);

            if (!requeued)
            {
                _logger.LogError("M3u8 download {JobId} failed permanently after being rate-limited", job.Id);
            }
        }
        catch (Exception ex)
        {
            // One bad URL must never take the worker down for the rest of the server's lifetime.
            var requeued = _queue.MarkAttemptFailed(
                job.Id,
                ex.Message,
                Math.Max(0, config.MaxRetries),
                TimeSpan.FromSeconds(Math.Max(0, config.RetryDelaySeconds)));

            if (requeued)
            {
                _logger.LogWarning(ex, "M3u8 download {JobId} failed; will retry", job.Id);
            }
            else
            {
                _logger.LogError(ex, "M3u8 download {JobId} failed permanently", job.Id);
            }
        }
        finally
        {
            _queue.ClearActiveCancellation(job.Id);

            // A courtesy gap so a queue of episodes from one site does not arrive as one unbroken
            // stream of requests. Extends rather than replaces any cooldown already set above, so
            // this cannot shorten a rate-limit backoff. The loop's bounded idle wait re-checks
            // often enough that the expiry needs no signal of its own.
            _queue.CoolDownHost(job.Url, TimeSpan.FromSeconds(Math.Max(0, config.DelayBetweenDownloadsSeconds)));
        }
    }

    /// <summary>
    /// Names the host a URL points at, for the log.
    /// </summary>
    /// <param name="url">The job URL.</param>
    /// <returns>The hostname, or the raw string when it will not parse.</returns>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// Tells Jellyfin a new file appeared, so it shows up without a manual scan.
    /// </summary>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="path">The finished file.</param>
    private void NotifyLibrary(PluginConfiguration config, string path)
    {
        if (!config.ScanLibraryAfterDownload)
        {
            return;
        }

        try
        {
            // Targeted notification rather than a full library validation: the server figures out
            // which library (if any) owns the path and refreshes just that.
            _libraryMonitor.ReportFileSystemChanged(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not notify the library about {Path}", path);
        }
    }
}
