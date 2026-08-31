namespace Jellyfin.Plugin.M3u8Downloader.Model;

/// <summary>
/// One reading from ffmpeg's <c>-progress</c> stream.
/// </summary>
/// <param name="PositionSeconds">
/// How far into the media ffmpeg has written, or <c>null</c> when the line carried no position.
/// </param>
/// <param name="SpeedRatio">
/// Progress relative to real time (1.0 is realtime), or <c>null</c> when the line carried no rate.
/// ffmpeg emits position and speed on separate lines, so at most one of these is set per line.
/// </param>
public record ProgressSample(double? PositionSeconds, double? SpeedRatio)
{
    /// <summary>Gets a value indicating whether this line carried anything worth reporting.</summary>
    public bool HasValue => PositionSeconds is not null || SpeedRatio is not null;
}
