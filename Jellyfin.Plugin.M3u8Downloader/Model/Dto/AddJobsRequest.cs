namespace Jellyfin.Plugin.M3u8Downloader.Model.Dto;

/// <summary>
/// Bulk-add request body.
/// </summary>
public class AddJobsRequest
{
    /// <summary>
    /// Gets or sets the raw textarea content, one job per line.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional destination folder, relative to the configured output directory,
    /// applied to every job in the batch.
    /// </summary>
    public string? Folder { get; set; }
}
