using System;
using Jellyfin.Plugin.M3u8Downloader.Model;

namespace Jellyfin.Plugin.M3u8Downloader.Model.Dto;

/// <summary>
/// The client-facing view of a <see cref="DownloadJob"/>.
/// </summary>
public class JobDto
{
    /// <summary>Gets or sets the job identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the source URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested output filename.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets this job's place in the run order. Lower runs sooner.</summary>
    public long QueuePosition { get; set; }

    /// <summary>Gets or sets the status name.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the completion percentage, if known.</summary>
    public double? ProgressPercent { get; set; }

    /// <summary>Gets or sets the position reached, in seconds.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Gets or sets the total duration in seconds, if known.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Gets or sets how fast the download is running relative to real time, when running.</summary>
    public double? SpeedRatio { get; set; }

    /// <summary>Gets or sets the number of attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the most recent error message.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets when the job was created.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets where a completed download was written, if it has finished.</summary>
    /// <remarks>
    /// Worth surfacing rather than leaving in the log: a name taken while the download was running
    /// makes the file land at " (2)", and the dashboard is where the person who queued it looks.
    /// </remarks>
    public string? ResolvedPath { get; set; }

    /// <summary>
    /// Projects a job onto its transport representation.
    /// </summary>
    /// <param name="job">The job to project.</param>
    /// <returns>The DTO.</returns>
    public static JobDto FromJob(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobDto
        {
            Id = job.Id,
            QueuePosition = job.QueuePosition ?? 0,
            Url = job.Url,
            FileName = job.RequestedFileName,
            Status = job.Status.ToString(),
            ProgressPercent = job.ProgressPercent,
            PositionSeconds = job.PositionSeconds,
            DurationSeconds = job.DurationSeconds,
            SpeedRatio = job.SpeedRatio,
            Attempts = job.Attempts,
            LastError = job.LastError,
            CreatedUtc = job.CreatedUtc,
            ResolvedPath = job.ResolvedPath,
        };
    }
}
