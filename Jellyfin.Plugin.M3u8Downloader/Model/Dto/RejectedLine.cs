namespace Jellyfin.Plugin.M3u8Downloader.Model.Dto;

/// <summary>
/// A bulk-input line that could not be turned into a job.
/// </summary>
/// <param name="LineNumber">The 1-based line number within the submitted text.</param>
/// <param name="Text">The offending line, verbatim.</param>
/// <param name="Error">Why the line was rejected.</param>
public record RejectedLine(int LineNumber, string Text, string Error);
