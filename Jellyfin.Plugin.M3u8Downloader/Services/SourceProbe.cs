using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Asks ffprobe to describe a source before it is downloaded.
/// </summary>
/// <remarks>
/// Behind <see cref="ISourceProbe"/> for the same reason <see cref="IFfmpegDownloader"/> exists:
/// what the caller does with the answer -- reuse a cached one, fall back when there is none -- is
/// the interesting part, and pinning it down should not require an ffprobe binary.
/// </remarks>
public sealed class SourceProbe : ISourceProbe
{
    /// <summary>Cap on how long ffprobe may spend measuring the duration.</summary>
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(60);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SourceProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceProbe"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Supplies the ffprobe path the server already uses.</param>
    /// <param name="logger">The logger.</param>
    public SourceProbe(IMediaEncoder mediaEncoder, ILogger<SourceProbe> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Asks ffprobe what it can about the source before the download starts.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>
    /// The duration and best program, either of which may be <c>null</c>. A failed probe is never
    /// fatal: without a duration the progress readout falls back to elapsed time, and without a
    /// program the download falls back to explicit per-type stream mapping.
    /// </returns>
    public async Task<SourceInfo> ProbeSourceAsync(string url, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.ProbePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in FfmpegArguments.BuildProbeArguments(url, config))
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyProxy(startInfo, config);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_probeTimeout);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new SourceInfo(null, null, LooksLikeHlsUrl(url));
            }

            var readTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

            // Drained even though "-v quiet" should keep it empty: an unread pipe that does fill up
            // blocks ffprobe until the timeout, turning a fast probe into a 60-second stall.
            var drainErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var json = await readTask.ConfigureAwait(false);
            await drainErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return new SourceInfo(null, null, LooksLikeHlsUrl(url));
            }

            using var document = JsonDocument.Parse(json);
            return new SourceInfo(
                ReadDuration(document.RootElement),
                ReadBestProgramId(document.RootElement),
                ReadIsHls(document.RootElement) || LooksLikeHlsUrl(url),
                ProbeSucceeded: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled the job; let the caller handle it.
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Could not probe {Url}", url);
        }

        return new SourceInfo(null, null, LooksLikeHlsUrl(url));
    }

    /// <summary>
    /// Determines whether ffprobe demuxed the input as HLS.
    /// </summary>
    /// <param name="root">The ffprobe JSON root.</param>
    /// <returns><c>true</c> when the HLS demuxer handled the input.</returns>
    private static bool ReadIsHls(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format)
            || !format.TryGetProperty("format_name", out var nameElement))
        {
            return false;
        }

        // format_name is a comma-separated list of candidate demuxers, e.g. "hls" or
        // "hls,applehttp" depending on the build.
        var name = nameElement.GetString();
        return name is not null
            && name.Split(',').Any(n => n.Trim().Equals("hls", StringComparison.OrdinalIgnoreCase)
                || n.Trim().Equals("applehttp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Falls back to the URL when the probe could not identify the demuxer.
    /// </summary>
    /// <param name="url">The source URL.</param>
    /// <returns><c>true</c> when the URL path names a playlist.</returns>
    /// <remarks>
    /// A probe can fail on a source that still downloads fine (a host that rejects ffprobe's
    /// request but not ffmpeg's, for instance). Losing the HLS tuning options in that case would
    /// mean losing exactly the robustness that a flaky host calls for.
    /// </remarks>
    private static bool LooksLikeHlsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the total duration out of an ffprobe document.
    /// </summary>
    /// <param name="root">The ffprobe JSON root.</param>
    /// <returns>The duration in seconds, or <c>null</c> when absent (e.g. a live stream).</returns>
    private static double? ReadDuration(JsonElement root)
    {
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationElement)
            && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return seconds;
        }

        return null;
    }

    /// <summary>
    /// Finds the program carrying the highest-resolution video.
    /// </summary>
    /// <param name="root">The ffprobe JSON root.</param>
    /// <returns>
    /// The program id to map, or <c>null</c> when the source has fewer than two programs — with a
    /// single program there is nothing to choose between, and plain per-type mapping is simpler
    /// and less likely to surprise.
    /// </returns>
    private static int? ReadBestProgramId(JsonElement root)
    {
        if (!root.TryGetProperty("programs", out var programs) || programs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (programs.GetArrayLength() < 2)
        {
            return null;
        }

        int? bestId = null;
        long bestPixels = -1;

        foreach (var program in programs.EnumerateArray())
        {
            if (!program.TryGetProperty("program_id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                continue;
            }

            if (!program.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type)
                    || !string.Equals(type.GetString(), "video", StringComparison.Ordinal))
                {
                    continue;
                }

                var pixels = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var width)
                    && stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var height)
                        ? (long)width * height
                        : 0;

                if (pixels > bestPixels)
                {
                    bestPixels = pixels;
                    bestId = id;
                }
            }
        }

        return bestId;
    }

    /// <summary>
    /// Points ffprobe at the configured proxy, reporting a value that cannot be used.
    /// </summary>
    /// <param name="startInfo">The child process about to be started.</param>
    /// <param name="config">Current plugin settings.</param>
    private void ApplyProxy(ProcessStartInfo startInfo, PluginConfiguration config)
    {
        var proxy = ProxySettings.ApplyProxyEnvironment(startInfo.Environment, config);

        if (proxy is not null)
        {
            _logger.LogDebug("Probing through HTTP proxy {Proxy}", proxy);
        }
    }

    /// <summary>
    /// Kills the ffprobe process, tolerating a race with normal exit.
    /// </summary>
    /// <param name="process">The process to kill.</param>
    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            _logger.LogDebug(ex, "Could not kill the ffprobe process; it likely already exited");
        }
    }
}
