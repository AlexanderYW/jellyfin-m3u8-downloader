using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Describes a source before it is downloaded.
/// </summary>
/// <remarks>
/// An interface so <see cref="FfmpegDownloader"/> can be exercised without an ffprobe binary: what
/// matters there is what it does with the answer, including whether it asks for one at all.
/// </remarks>
public interface ISourceProbe
{
    /// <summary>
    /// Asks ffprobe what it can about a source.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>
    /// What was learned. A failed probe is never fatal, and reports itself as such through
    /// <see cref="SourceInfo.ProbeSucceeded"/>.
    /// </returns>
    Task<SourceInfo> ProbeSourceAsync(string url, PluginConfiguration config, CancellationToken cancellationToken);
}
