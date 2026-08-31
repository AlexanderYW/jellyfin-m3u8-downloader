namespace Jellyfin.Plugin.M3u8Downloader.Model;

/// <summary>
/// What ffprobe could tell us about a source before the download starts.
/// </summary>
/// <param name="DurationSeconds">
/// Total duration, or <c>null</c> for a live or open-ended stream.
/// </param>
/// <param name="BestProgramId">
/// The HLS program holding the highest-resolution video, or <c>null</c> when the source does not
/// expose multiple programs. See <see cref="Services.FfmpegDownloader.BuildDownloadArguments"/>
/// for why this drives stream selection.
/// </param>
/// <param name="IsHls">
/// Whether the input is served by ffmpeg's HLS demuxer. Gates the HLS-only tuning options, which
/// ffmpeg rejects outright ("Option not found") on any other demuxer.
/// </param>
public record SourceInfo(double? DurationSeconds, int? BestProgramId, bool IsHls);
