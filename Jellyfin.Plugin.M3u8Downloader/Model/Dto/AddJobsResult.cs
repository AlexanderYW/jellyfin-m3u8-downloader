using System.Collections.Generic;

namespace Jellyfin.Plugin.M3u8Downloader.Model.Dto;

/// <summary>
/// Outcome of a bulk add: what was queued, and what was not.
/// </summary>
public class AddJobsResult
{
    /// <summary>
    /// Gets the jobs that were accepted and queued.
    /// </summary>
    public IReadOnlyList<JobDto> Added { get; init; } = new List<JobDto>();

    /// <summary>
    /// Gets the lines that were rejected, with the reason for each.
    /// </summary>
    public IReadOnlyList<RejectedLine> Rejected { get; init; } = new List<RejectedLine>();
}
