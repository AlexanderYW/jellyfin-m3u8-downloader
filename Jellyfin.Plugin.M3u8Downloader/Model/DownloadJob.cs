using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.M3u8Downloader.Model;

/// <summary>
/// A single m3u8 download request and its current state.
/// </summary>
/// <remarks>
/// Instances are owned by <see cref="Services.DownloadQueueService"/> and mutated only while
/// holding its lock, with the exception of the live progress fields which the downloader updates
/// on the running job.
/// </remarks>
public class DownloadJob
{
    /// <summary>
    /// Gets or sets the unique identifier for this job.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the source m3u8 URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filename the user asked for, relative to the output directory.
    /// </summary>
    public string RequestedFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute output path, resolved when the download starts.
    /// </summary>
    public string? ResolvedPath { get; set; }

    /// <summary>
    /// Gets or sets this job's place in the queue. Lower runs sooner.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreatedUtc"/> so a job can be moved to the front without rewriting
    /// when it was created. Nullable because moving a job to the front walks the numbering
    /// downwards and will reach zero: "no position yet" therefore has to be a value of its own,
    /// or a job promoted to the front would look like one restored from a queue file written
    /// before positions existed.
    /// </remarks>
    public long? QueuePosition { get; set; }

    /// <summary>
    /// Gets or sets the current lifecycle state.
    /// </summary>
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>
    /// Gets or sets the total media duration in seconds, if ffprobe could determine it.
    /// </summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the HLS program ffprobe picked out as carrying the best video, if any.
    /// </summary>
    /// <remarks>
    /// Part of the cached probe result; see <see cref="ProbedUtc"/>.
    /// </remarks>
    public int? ProbedProgramId { get; set; }

    /// <summary>
    /// Gets or sets whether the probed source turned out to be HLS.
    /// </summary>
    /// <remarks>
    /// Part of the cached probe result; see <see cref="ProbedUtc"/>.
    /// </remarks>
    public bool? ProbedIsHls { get; set; }

    /// <summary>
    /// Gets or sets when ffprobe last described this source successfully.
    /// </summary>
    /// <remarks>
    /// Its presence is what marks the probe fields as usable, and it is what lets an automatic
    /// retry skip the probe entirely. That matters most after a rate limit: the probe would
    /// otherwise be the first request back to a host still refusing us, and if it failed, stream
    /// selection would fall back to the no-map path for a source we had already described
    /// correctly. Cleared by an explicit user retry, which may well be a response to the source
    /// having changed.
    /// </remarks>
    public DateTime? ProbedUtc { get; set; }

    /// <summary>
    /// Gets or sets how far into the media ffmpeg has written, in seconds.
    /// </summary>
    public double PositionSeconds { get; set; }

    /// <summary>
    /// Gets or sets how fast the running download is progressing relative to real time, as ffmpeg
    /// reports it: 1.0 is realtime, 10.0 is ten times faster.
    /// </summary>
    /// <remarks>
    /// Live only, and cleared whenever the job stops running: a rate left over from a finished or
    /// re-queued attempt would be read as the current one.
    /// </remarks>
    public double? SpeedRatio { get; set; }

    /// <summary>
    /// Gets the completion percentage, or null when the duration is unknown (e.g. a live stream).
    /// </summary>
    [JsonIgnore]
    public double? ProgressPercent =>
        DurationSeconds is > 0
            ? Math.Clamp(PositionSeconds / DurationSeconds.Value * 100d, 0d, 100d)
            : null;

    /// <summary>
    /// Gets or sets the number of attempts made so far.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets the error from the most recent failed attempt.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the earliest time this job may be started again, used for retry backoff.
    /// </summary>
    public DateTime? NotBeforeUtc { get; set; }

    /// <summary>
    /// Gets or sets when the job was added to the queue.
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets when the current (or most recent) attempt started.
    /// </summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the job reached a terminal state.
    /// </summary>
    public DateTime? CompletedUtc { get; set; }
}
