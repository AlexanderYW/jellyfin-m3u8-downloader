using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Runs a single download to completion.
/// </summary>
/// <remarks>
/// An interface purely so <see cref="QueueWorker"/> can be exercised without spawning ffmpeg: the
/// worker's job is to turn outcomes into queue state, and that is what the tests need to pin down.
/// </remarks>
public interface IFfmpegDownloader
{
    /// <summary>
    /// Downloads a job to completion.
    /// </summary>
    /// <param name="job">The job to run.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="cancellationToken">Cancels the download and kills ffmpeg.</param>
    /// <returns>The absolute path of the finished file.</returns>
    Task<string> RunAsync(DownloadJob job, PluginConfiguration config, CancellationToken cancellationToken);
}
