namespace Jellyfin.Plugin.M3u8Downloader.Model;

/// <summary>
/// What ffprobe could tell us about a source before the download starts.
/// </summary>
/// <param name="DurationSeconds">
/// Total duration, or <c>null</c> for a live or open-ended stream.
/// </param>
/// <param name="BestProgramId">
/// The HLS program holding the highest-resolution video, or <c>null</c> when the source does not
/// expose multiple programs. See <see cref="Services.FfmpegArguments.BuildDownloadArguments"/>
/// for why this drives stream selection.
/// </param>
/// <param name="IsHls">
/// Whether the input is served by ffmpeg's HLS demuxer. Gates the HLS-only tuning options, which
/// ffmpeg rejects outright ("Option not found") on any other demuxer.
/// </param>
/// <param name="ProbeSucceeded">
/// Whether ffprobe actually described the source, as opposed to the defaults returned when it
/// failed, timed out, or produced unparseable output.
///
/// This is not the same question as <see cref="BestProgramId"/> being <c>null</c>, and conflating
/// the two is what let one job hammer a host: a probe that succeeded and reported a single program
/// genuinely has no renditions to choose between, whereas a probe that failed tells us nothing --
/// and on an HLS master playlist, mapping every video stream then makes ffmpeg pull every bitrate
/// rendition at once. See <see cref="Services.FfmpegArguments.BuildDownloadArguments"/>.
/// </param>
public record SourceInfo(double? DurationSeconds, int? BestProgramId, bool IsHls, bool ProbeSucceeded = false);
