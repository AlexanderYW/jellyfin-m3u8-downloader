using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// In-memory queue backed by a JSON file in the plugin's data folder.
/// </summary>
/// <remarks>
/// Singleton. All mutation happens under <see cref="_lock"/>; every state transition is flushed to
/// disk so the queue survives a server restart. Live progress updates deliberately skip the flush
/// to avoid hammering the disk once a second.
/// </remarks>
public sealed class DownloadQueueService : IDownloadQueueService, IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ILogger<DownloadQueueService> _logger;
    private readonly string _statePath;

    /// <summary>Cancellation for each running job, keyed by job id.</summary>
    /// <remarks>
    /// A dictionary rather than a single slot because more than one download can be in flight;
    /// cancelling one job must not disturb another.
    /// </remarks>
    private readonly Dictionary<Guid, CancellationTokenSource> _active = new();

    /// <summary>Jobs a caller asked to cancel before the worker registered their cancellation.</summary>
    /// <remarks>
    /// A job is marked <see cref="JobStatus.Downloading"/> the moment it is claimed, but its
    /// cancellation source only arrives once the worker has started running it. A cancel landing in
    /// that window would otherwise find nothing to cancel and report success while the download ran
    /// on. Recording the intent here lets <see cref="SetActiveCancellation"/> honour it as soon as
    /// there is something to cancel.
    /// </remarks>
    private readonly HashSet<Guid> _cancelRequested = new();

    /// <summary>Earliest UTC time a new download may start against each hostname.</summary>
    /// <remarks>
    /// Deliberately not persisted: a cooldown is a courtesy towards a host over the next few
    /// minutes, and a server that has just restarted has not sent it anything anyway. Entries are
    /// pruned as they expire in <see cref="TryDequeueNext"/>, so this stays bounded by the number
    /// of hosts currently in play rather than by everything ever downloaded.
    /// </remarks>
    private readonly Dictionary<string, DateTime> _hostCooldowns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serialises the actual file write, which happens outside <see cref="_lock"/>.</summary>
    private readonly object _writeLock = new();

    private long _nextPosition = 1;
    private long _saveSequence;
    private long _lastWrittenSequence;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadQueueService"/> class, persisting to
    /// the plugin's data folder.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DownloadQueueService(ILogger<DownloadQueueService> logger)
        : this(logger, Path.Combine(DefaultDataFolder(), "queue.json"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadQueueService"/> class with an explicit
    /// state file.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="statePath">Where the queue is persisted.</param>
    /// <remarks>
    /// The path is a parameter so the queue can be exercised against a temporary directory in
    /// tests; the server resolves it from <see cref="Plugin.Instance"/> through the other
    /// constructor.
    /// </remarks>
    public DownloadQueueService(ILogger<DownloadQueueService> logger, string statePath)
    {
        _logger = logger;
        _statePath = statePath;

        var dataFolder = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(dataFolder))
        {
            Directory.CreateDirectory(dataFolder);
        }

        Load();
    }

    /// <summary>
    /// Resolves the plugin's data folder, falling back to the temp directory before the plugin has
    /// been constructed.
    /// </summary>
    /// <returns>The directory the queue file lives in.</returns>
    private static string DefaultDataFolder() => Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();

    /// <inheritdoc />
    public IReadOnlyList<DownloadJob> GetAll()
    {
        List<DownloadJob> snapshot;

        lock (_lock)
        {
            // Hand back detached copies: callers serialise these outside the lock, and the worker
            // mutates progress on the live instances concurrently. Only the copying needs the lock
            // -- ordering a list nobody else can see does not, and this is polled every two
            // seconds against the same lock the worker claims jobs under.
            snapshot = _jobs.Select(Clone).ToList();
        }

        return snapshot.OrderByDescending(j => j.CreatedUtc).ToList();
    }

    /// <inheritdoc />
    public void AddRange(IEnumerable<DownloadJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var added = 0;
        PendingSave? pending = null;

        lock (_lock)
        {
            foreach (var job in jobs)
            {
                job.QueuePosition = _nextPosition++;
                _jobs.Add(job);
                added++;
            }

            if (added > 0)
            {
                pending = CaptureSave();
            }
        }

        Flush(pending);

        if (added > 0)
        {
            Signal();
        }
    }

    /// <inheritdoc />
    public DownloadJob? TryDequeueNext(int maxConcurrent, int maxPerHost = 1)
    {
        DownloadJob? next;
        PendingSave pending;

        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // Claiming is what enforces the concurrency limit: counting under the same lock that
            // marks a job Downloading is what stops two workers from both seeing a free slot.
            if (_jobs.Count(j => j.Status == JobStatus.Downloading) >= Math.Max(1, maxConcurrent))
            {
                return null;
            }

            // An expired cooldown has no further say, and dropping it here is what keeps the
            // dictionary bounded without a timer of its own.
            foreach (var expired in _hostCooldowns.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            {
                _hostCooldowns.Remove(expired);
            }

            // The per-host limit is a filter on candidates rather than an early return, so a busy
            // or cooling host holds up only its own jobs: the queue walks past them in position
            // order and claims the next job on a host that is free.
            var perHostLimit = Math.Max(1, maxPerHost);
            var running = _jobs
                .Where(j => j.Status == JobStatus.Downloading)
                .GroupBy(j => HostOf(j.Url), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            next = _jobs
                .Where(j => j.Status == JobStatus.Queued && (j.NotBeforeUtc is null || j.NotBeforeUtc <= now))
                .Where(j => IsHostFree(HostOf(j.Url), running, perHostLimit))
                .OrderBy(j => j.QueuePosition ?? long.MaxValue)
                .FirstOrDefault();

            if (next is null)
            {
                return null;
            }

            next.Status = JobStatus.Downloading;
            next.StartedUtc = now;
            next.NotBeforeUtc = null;
            next.PositionSeconds = 0;
            next.SpeedRatio = null;
            pending = CaptureSave();
        }

        Flush(pending);

        return next;
    }

    /// <inheritdoc />
    public void ReportProgress(Guid jobId, double positionSeconds, double? durationSeconds, double? speedRatio = null)
    {
        lock (_lock)
        {
            var job = Find(jobId);
            if (job is null)
            {
                return;
            }

            job.PositionSeconds = positionSeconds;
            if (durationSeconds is > 0)
            {
                job.DurationSeconds = durationSeconds;
            }

            // ffmpeg emits speed on its own progress lines, so a sample carrying only a position
            // must not blank out the last rate we knew about.
            if (speedRatio is > 0)
            {
                job.SpeedRatio = speedRatio;
            }
        }
    }

    /// <inheritdoc />
    public void MarkCompleted(Guid jobId, string resolvedPath)
    {
        PendingSave? pending = null;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is not null)
            {
                job.Status = JobStatus.Completed;
                job.ResolvedPath = resolvedPath;
                job.CompletedUtc = DateTime.UtcNow;
                job.LastError = null;
                job.SpeedRatio = null;

                // Snap the bar to 100% even when the probed duration was slightly off.
                if (job.DurationSeconds is > 0)
                {
                    job.PositionSeconds = job.DurationSeconds.Value;
                }

                pending = CaptureSave();
            }
        }

        Flush(pending);
    }

    /// <inheritdoc />
    public void MarkCanceled(Guid jobId)
    {
        PendingSave? pending = null;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is not null)
            {
                job.Status = JobStatus.Canceled;
                job.CompletedUtc = DateTime.UtcNow;
                job.SpeedRatio = null;
                pending = CaptureSave();
            }
        }

        Flush(pending);
    }

    /// <inheritdoc />
    public void CoolDownHost(string url, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var until = DateTime.UtcNow.Add(duration);
        var host = HostOf(url);

        lock (_lock)
        {
            // Extend only. The courtesy gap after a download finishes must never shorten the long
            // backoff a rate limit just imposed on the same host.
            if (!_hostCooldowns.TryGetValue(host, out var existing) || until > existing)
            {
                _hostCooldowns[host] = until;
            }
        }
    }

    /// <summary>
    /// Decides whether another download may start against a host right now.
    /// </summary>
    /// <param name="host">The hostname, as returned by <see cref="HostOf"/>.</param>
    /// <param name="running">How many downloads are in flight per host.</param>
    /// <param name="maxPerHost">The per-host limit, already floored at 1.</param>
    /// <returns><c>true</c> when the host is neither at its limit nor cooling down.</returns>
    /// <remarks>Callers must hold <see cref="_lock"/>: it reads <see cref="_hostCooldowns"/>.</remarks>
    private bool IsHostFree(string host, Dictionary<string, int> running, int maxPerHost)
    {
        if (_hostCooldowns.ContainsKey(host))
        {
            // Expired entries were already pruned by the caller, so anything still here is live.
            return false;
        }

        return !running.TryGetValue(host, out var count) || count < maxPerHost;
    }

    /// <summary>
    /// Extracts the hostname a job's URL points at.
    /// </summary>
    /// <param name="url">The job URL.</param>
    /// <returns>The hostname, or the raw string when it will not parse.</returns>
    /// <remarks>
    /// Falling back to the whole URL is defensive rather than expected -- the API validates that a
    /// job URL is http or https before it is ever queued. Treating an unparseable URL as its own
    /// host keeps it isolated: it can neither join another host's budget nor escape a limit.
    /// </remarks>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <inheritdoc />
    public bool MarkAttemptFailed(Guid jobId, string error, int maxRetries, TimeSpan retryDelay)
    {
        var requeued = false;
        PendingSave pending;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is null)
            {
                return false;
            }

            job.Attempts++;
            job.LastError = error;
            job.SpeedRatio = null;

            if (job.Attempts <= maxRetries)
            {
                job.Status = JobStatus.Queued;
                job.NotBeforeUtc = DateTime.UtcNow.Add(retryDelay);
                job.StartedUtc = null;
                requeued = true;
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.CompletedUtc = DateTime.UtcNow;
            }

            pending = CaptureSave();
        }

        Flush(pending);

        return requeued;
    }

    /// <inheritdoc />
    public bool CancelOrRemove(Guid jobId)
    {
        CancellationTokenSource? toCancel = null;
        PendingSave? pending = null;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is null)
            {
                return false;
            }

            if (job.Status == JobStatus.Downloading)
            {
                // Let the worker do the state transition once ffmpeg actually dies; cancelling
                // here would race with the download's own cleanup.
                if (!_active.TryGetValue(jobId, out toCancel))
                {
                    // Claimed but not yet running: the worker has not registered its cancellation
                    // source. Leave the request for SetActiveCancellation to pick up, rather than
                    // reporting a cancel that never happens.
                    _cancelRequested.Add(jobId);
                }
            }
            else
            {
                _jobs.Remove(job);
                pending = CaptureSave();
            }
        }

        Flush(pending);

        if (toCancel is not null)
        {
            try
            {
                toCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished on its own between our lock release and the cancel.
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Retry(Guid jobId)
    {
        PendingSave pending;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is null || job.Status is JobStatus.Queued or JobStatus.Downloading)
            {
                return false;
            }

            job.Status = JobStatus.Queued;
            job.Attempts = 0;
            job.LastError = null;
            job.NotBeforeUtc = null;
            job.StartedUtc = null;
            job.CompletedUtc = null;
            job.PositionSeconds = 0;
            job.SpeedRatio = null;
            pending = CaptureSave();
        }

        Flush(pending);
        Signal();
        return true;
    }

    /// <inheritdoc />
    public bool MoveToTop(Guid jobId)
    {
        PendingSave? pending = null;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is null || job.Status != JobStatus.Queued)
            {
                return false;
            }

            // One below the current front, rather than a renumbering pass: the position only has
            // to order the queue, and it is a long, so it can keep descending indefinitely.
            var front = _jobs.Min(j => j.QueuePosition ?? long.MaxValue);
            if (job.QueuePosition != front)
            {
                job.QueuePosition = front - 1;
                pending = CaptureSave();
            }
        }

        Flush(pending);
        return true;
    }

    /// <inheritdoc />
    public bool MoveToBottom(Guid jobId)
    {
        PendingSave? pending = null;

        lock (_lock)
        {
            var job = Find(jobId);
            if (job is null || job.Status != JobStatus.Queued)
            {
                return false;
            }

            // The mirror of MoveToTop: one past the current back. _nextPosition has to follow it
            // so a job added afterwards still lands behind this one.
            var back = _jobs.Max(j => j.QueuePosition ?? long.MinValue);
            if (job.QueuePosition != back)
            {
                job.QueuePosition = back + 1;
                _nextPosition = Math.Max(_nextPosition, back + 2);
                pending = CaptureSave();
            }
        }

        Flush(pending);
        return true;
    }

    /// <inheritdoc />
    public int PruneHistory(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        int removed;
        PendingSave? pending = null;

        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            // Only finished jobs, and only those that recorded when they finished: a terminal job
            // with no CompletedUtc predates this field, and dropping it on a guess is worse than
            // leaving it for the user to clear.
            removed = _jobs.RemoveAll(j =>
                j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled
                && j.CompletedUtc is not null
                && j.CompletedUtc < cutoff);

            if (removed > 0)
            {
                _logger.LogInformation("Pruned {Count} finished M3U8 download job(s) older than {Days} day(s)", removed, retentionDays);
                pending = CaptureSave();
            }
        }

        Flush(pending);

        return removed;
    }

    /// <inheritdoc />
    public int ClearFinished()
    {
        int removed;
        PendingSave? pending = null;

        lock (_lock)
        {
            removed = _jobs.RemoveAll(j =>
                j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled);

            if (removed > 0)
            {
                pending = CaptureSave();
            }
        }

        Flush(pending);

        return removed;
    }

    /// <inheritdoc />
    public void SetActiveCancellation(Guid jobId, CancellationTokenSource cts)
    {
        bool cancelNow;

        lock (_lock)
        {
            _active[jobId] = cts;

            // A cancel that arrived while the job was claimed but not yet running was parked; this
            // is the first moment it can be acted on.
            cancelNow = _cancelRequested.Remove(jobId);
        }

        if (cancelNow)
        {
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job finished on its own between our lock release and the cancel.
            }
        }
    }

    /// <inheritdoc />
    public void ClearActiveCancellation(Guid jobId)
    {
        lock (_lock)
        {
            _active.Remove(jobId);

            // A retry reuses the job id, so a request left over from an attempt that already ended
            // must not cancel the next one.
            _cancelRequested.Remove(jobId);
        }
    }

    /// <inheritdoc />
    public async Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; the worker loop checks the token itself.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signal.Dispose();
    }

    /// <summary>
    /// Releases a waiting worker.
    /// </summary>
    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the worker will see the new work on its next pass.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Finds a job by id. Caller must hold <see cref="_lock"/>.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The live job instance, or <c>null</c>.</returns>
    private DownloadJob? Find(Guid jobId) => _jobs.Find(j => j.Id == jobId);

    private static DownloadJob Clone(DownloadJob job) => new()
    {
        Id = job.Id,
        QueuePosition = job.QueuePosition,
        Url = job.Url,
        RequestedFileName = job.RequestedFileName,
        ResolvedPath = job.ResolvedPath,
        Status = job.Status,
        DurationSeconds = job.DurationSeconds,
        PositionSeconds = job.PositionSeconds,
        SpeedRatio = job.SpeedRatio,
        Attempts = job.Attempts,
        LastError = job.LastError,
        NotBeforeUtc = job.NotBeforeUtc,
        CreatedUtc = job.CreatedUtc,
        StartedUtc = job.StartedUtc,
        CompletedUtc = job.CompletedUtc,
    };

    /// <summary>
    /// Restores the queue from disk. Caller must not hold <see cref="_lock"/>.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var restored = JsonSerializer.Deserialize<List<DownloadJob>>(json, _jsonOptions);
            if (restored is null)
            {
                return;
            }

            foreach (var job in restored)
            {
                // A job left mid-download can only mean the server died. HLS remuxing cannot be
                // resumed mid-file, so the attempt starts over.
                if (job.Status == JobStatus.Downloading)
                {
                    job.Status = JobStatus.Queued;
                    job.StartedUtc = null;
                    job.PositionSeconds = 0;
                    job.SpeedRatio = null;
                }

                _jobs.Add(job);
            }

            SeedQueuePositions();

            _logger.LogInformation("Restored {Count} M3U8 download job(s) from {Path}", _jobs.Count, _statePath);

            if (_jobs.Any(j => j.Status == JobStatus.Queued))
            {
                Signal();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the M3U8 download queue from {Path}; starting empty", _statePath);
        }
    }

    /// <summary>
    /// Assigns queue positions to restored jobs and continues the counter from the highest one.
    /// </summary>
    /// <remarks>
    /// A queue file written before jobs carried a position deserialises with every position unset.
    /// Numbering those by creation time reproduces exactly the order the old build ran them in, so
    /// an upgrade with work still queued does not reshuffle it.
    /// </remarks>
    private void SeedQueuePositions()
    {
        var unplaced = _jobs.Where(j => j.QueuePosition is null).OrderBy(j => j.CreatedUtc).ToList();
        var next = _jobs.Count > 0 ? _jobs.Max(j => j.QueuePosition ?? 0) + 1 : 1;

        foreach (var job in unplaced)
        {
            job.QueuePosition = next++;
        }

        _nextPosition = next;
    }

    /// <summary>
    /// Serialises the queue for persistence. Caller must hold <see cref="_lock"/>.
    /// </summary>
    /// <returns>The snapshot to hand to <see cref="Flush"/> once the lock has been released.</returns>
    /// <remarks>
    /// Only the copy happens under the lock. Serialising the queue and writing it are both
    /// comparatively slow, and doing either here would block every reader and the worker's next
    /// claim on each state transition -- with several downloads running and a long history, the
    /// serialisation is the expensive half. Detached copies are what make it safe to serialise
    /// afterwards, since the worker keeps mutating the live instances.
    /// </remarks>
    private PendingSave CaptureSave() => new(++_saveSequence, _jobs.Select(Clone).ToList());

    /// <summary>
    /// Writes a captured snapshot to disk. Caller must not hold <see cref="_lock"/>.
    /// </summary>
    /// <param name="pending">The snapshot, or <c>null</c> when nothing changed.</param>
    private void Flush(PendingSave? pending)
    {
        if (pending is not { } save)
        {
            return;
        }

        lock (_writeLock)
        {
            // Two transitions can reach here in either order once the queue lock is no longer
            // serialising the writes. The sequence number is what stops the older snapshot from
            // landing last and undoing the newer one -- and checking it before serialising means a
            // superseded snapshot costs nothing at all.
            if (save.Sequence < _lastWrittenSequence)
            {
                return;
            }

            // Write-then-move so a crash mid-write cannot leave a truncated queue file behind.
            var tempPath = _statePath + ".tmp";

            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(save.Jobs, _jsonOptions));
                File.Move(tempPath, _statePath, overwrite: true);
                _lastWrittenSequence = save.Sequence;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
            {
                _logger.LogError(ex, "Could not persist the M3U8 download queue to {Path}", _statePath);

                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException)
                {
                    // Nothing more we can do.
                }
            }
        }
    }

    /// <summary>
    /// A serialised queue snapshot waiting to be written to disk.
    /// </summary>
    /// <param name="Sequence">Monotonic order in which the snapshot was taken.</param>
    /// <param name="Jobs">Detached copies of the queue, safe to serialise off the lock.</param>
    private readonly record struct PendingSave(long Sequence, List<DownloadJob> Jobs);
}
