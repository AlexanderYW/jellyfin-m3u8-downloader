namespace Jellyfin.Plugin.M3u8Downloader.Model;

/// <summary>
/// Lifecycle state of a download job.
/// </summary>
public enum JobStatus
{
    /// <summary>Waiting for the worker to pick it up.</summary>
    Queued = 0,

    /// <summary>Currently being downloaded by ffmpeg.</summary>
    Downloading = 1,

    /// <summary>Finished successfully; the output file is in place.</summary>
    Completed = 2,

    /// <summary>Gave up after exhausting the configured retries.</summary>
    Failed = 3,

    /// <summary>Cancelled by the user.</summary>
    Canceled = 4,
}
