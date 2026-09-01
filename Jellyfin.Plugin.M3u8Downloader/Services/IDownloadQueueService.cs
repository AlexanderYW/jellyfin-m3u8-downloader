using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Model;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// The authoritative, persisted download queue.
/// </summary>
public interface IDownloadQueueService
{
    /// <summary>
    /// Returns a snapshot of every job, newest first.
    /// </summary>
    /// <returns>Detached copies safe to serialise without holding the queue lock.</returns>
    IReadOnlyList<DownloadJob> GetAll();

    /// <summary>
    /// Queues a batch of jobs and wakes the worker.
    /// </summary>
    /// <param name="jobs">The jobs to add.</param>
    void AddRange(IEnumerable<DownloadJob> jobs);

    /// <summary>
    /// Claims the next runnable job, transitioning it to <see cref="JobStatus.Downloading"/>.
    /// </summary>
    /// <param name="maxConcurrent">
    /// How many downloads may run at once. Nothing is claimed once that many are in flight.
    /// </param>
    /// <param name="maxPerHost">
    /// How many downloads may run against one hostname at once. A job whose host is already at
    /// this limit, or is cooling down, is skipped rather than blocking the queue -- the next
    /// runnable job on another host is claimed instead.
    /// </param>
    /// <returns>The claimed job, or <c>null</c> when nothing is runnable right now.</returns>
    DownloadJob? TryDequeueNext(int maxConcurrent, int maxPerHost = 1);

    /// <summary>
    /// Holds back every job on a URL's host until the given time has passed.
    /// </summary>
    /// <param name="url">Any URL on the host; only its hostname is used.</param>
    /// <param name="duration">How long to wait. A non-positive value does nothing.</param>
    /// <remarks>
    /// Used both for the short courtesy gap between consecutive downloads from one site and for
    /// the long pause after that site rate-limits us. An existing cooldown is only ever extended,
    /// never cut short, so a five-second gap cannot cancel a thirty-minute backoff.
    /// </remarks>
    void CoolDownHost(string url, TimeSpan duration);

    /// <summary>
    /// Stores what ffprobe learned about a job's source, so a retry need not ask again.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="durationSeconds">The measured duration, if any.</param>
    /// <param name="bestProgramId">The program carrying the best video, if the source has a choice.</param>
    /// <param name="isHls">Whether the source is HLS.</param>
    /// <remarks>
    /// Persisted, unlike live progress: the point is precisely that it survives a re-queue, and a
    /// probe is one request to a host that may already be refusing us.
    /// </remarks>
    void RecordProbe(Guid jobId, double? durationSeconds, int? bestProgramId, bool isHls);

    /// <summary>
    /// Records live progress for a running job. Not persisted; flushed on the next transition.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="positionSeconds">Seconds of media written so far.</param>
    /// <param name="durationSeconds">Total duration, when known.</param>
    /// <param name="speedRatio">
    /// How fast the download is running relative to real time, when the latest progress line
    /// carried it. <c>null</c> leaves the previous value in place rather than clearing it.
    /// </param>
    void ReportProgress(Guid jobId, double positionSeconds, double? durationSeconds, double? speedRatio = null);

    /// <summary>
    /// Marks a job completed.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="resolvedPath">Where the finished file was written.</param>
    void MarkCompleted(Guid jobId, string resolvedPath);

    /// <summary>
    /// Marks a job cancelled.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    void MarkCanceled(Guid jobId);

    /// <summary>
    /// Records an attempt failure, re-queueing with backoff while retries remain.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="error">The failure message to surface.</param>
    /// <param name="maxRetries">How many retries are permitted.</param>
    /// <param name="retryDelay">How long to wait before the next attempt.</param>
    /// <returns><c>true</c> if the job was re-queued, <c>false</c> if it was marked failed.</returns>
    bool MarkAttemptFailed(Guid jobId, string error, int maxRetries, TimeSpan retryDelay);

    /// <summary>
    /// Cancels the job if it is running, otherwise removes it from the queue.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns><c>true</c> when a matching job was found.</returns>
    bool CancelOrRemove(Guid jobId);

    /// <summary>
    /// Puts a failed or cancelled job back in the queue with its attempt counter reset.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns><c>true</c> when the job was re-queued.</returns>
    bool Retry(Guid jobId);

    /// <summary>
    /// Moves a queued job to the front of the queue.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns><c>true</c> when the job was queued and is now first in line.</returns>
    bool MoveToTop(Guid jobId);

    /// <summary>
    /// Moves a queued job to the back of the queue.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns><c>true</c> when the job was queued and is now last in line.</returns>
    bool MoveToBottom(Guid jobId);

    /// <summary>
    /// Drops finished jobs that completed longer ago than the retention window.
    /// </summary>
    /// <param name="retentionDays">Days of history to keep; zero or less keeps everything.</param>
    /// <returns>How many jobs were removed.</returns>
    int PruneHistory(int retentionDays);

    /// <summary>
    /// Drops every job in a terminal state.
    /// </summary>
    /// <returns>How many jobs were removed.</returns>
    int ClearFinished();

    /// <summary>
    /// Associates a cancellation source with the currently running job.
    /// </summary>
    /// <param name="jobId">The running job identifier.</param>
    /// <param name="cts">The source to cancel when the user aborts the job.</param>
    void SetActiveCancellation(Guid jobId, CancellationTokenSource cts);

    /// <summary>
    /// Clears the active cancellation registration.
    /// </summary>
    /// <param name="jobId">The job that finished.</param>
    void ClearActiveCancellation(Guid jobId);

    /// <summary>
    /// Waits until work may be available, or the timeout elapses.
    /// </summary>
    /// <param name="timeout">Maximum time to wait, bounding the retry-backoff poll.</param>
    /// <param name="cancellationToken">Stops the wait on shutdown.</param>
    /// <returns>A task that completes when the worker should look again.</returns>
    Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
