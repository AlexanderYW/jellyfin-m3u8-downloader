using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

/// <summary>
/// Covers what the downloader does about probing, which is the part that survives a retry.
/// </summary>
/// <remarks>
/// Each test drives RunAsync far enough to see whether a probe was requested and no further:
/// FakeMediaEncoder points at an ffmpeg that is not there, so the download itself always fails.
/// That failure is the boundary of what these tests assert, not an accident.
/// </remarks>
public sealed class FfmpegDownloaderProbeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "m3u8-probe-" + Guid.NewGuid().ToString("N"));

    private readonly CountingProbe _probe = new();
    private readonly DownloadQueueService _queue;
    private readonly FfmpegDownloader _downloader;
    private readonly PluginConfiguration _config;

    public FfmpegDownloaderProbeTests()
    {
        Directory.CreateDirectory(_directory);
        _config = new PluginConfiguration { OutputDirectory = _directory, StallTimeoutMinutes = 0 };
        _queue = new DownloadQueueService(
            NullLogger<DownloadQueueService>.Instance,
            Path.Combine(_directory, "queue.json"));
        _downloader = new FfmpegDownloader(
            new FakeMediaEncoder(),
            _queue,
            _probe,
            new OutputFilePublisher(NullLogger<OutputFilePublisher>.Instance),
            NullLogger<FfmpegDownloader>.Instance);
    }

    public void Dispose()
    {
        _queue.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task RunAsync_WithoutACachedProbe_AsksTheHost()
    {
        var job = NewJob();

        await RunIgnoringTheMissingFfmpegAsync(job);

        Assert.Equal(1, _probe.Calls);
    }

    [Fact]
    public async Task RunAsync_WithACachedProbe_DoesNotTouchTheHostAgain()
    {
        // The point of caching it. After a rate limit the probe would otherwise be the first
        // request back to a host that is still refusing us.
        var job = NewJob();
        job.ProbedUtc = DateTime.UtcNow;
        job.ProbedProgramId = 2;
        job.ProbedIsHls = true;
        job.DurationSeconds = 90;

        await RunIgnoringTheMissingFfmpegAsync(job);

        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task RunAsync_RecordsASuccessfulProbeOnTheJob()
    {
        var job = NewJob();
        _queue.AddRange(new[] { job });
        _probe.Result = new SourceInfo(120, 4, IsHls: true, ProbeSucceeded: true);

        await RunIgnoringTheMissingFfmpegAsync(job);

        var stored = _queue.GetAll().Single(j => j.Id == job.Id);
        Assert.Equal(4, stored.ProbedProgramId);
        Assert.True(stored.ProbedIsHls);
        Assert.Equal(120, stored.DurationSeconds);
        Assert.NotNull(stored.ProbedUtc);
    }

    [Fact]
    public async Task RunAsync_DoesNotCacheAFailedProbe()
    {
        // A probe that told us nothing must not be remembered as though it had.
        var job = NewJob();
        _queue.AddRange(new[] { job });
        _probe.Result = new SourceInfo(null, null, IsHls: true);

        await RunIgnoringTheMissingFfmpegAsync(job);

        Assert.Null(_queue.GetAll().Single(j => j.Id == job.Id).ProbedUtc);
    }

    private DownloadJob NewJob() => new()
    {
        Url = "https://example.com/stream.m3u8",
        RequestedFileName = "Episode",
    };

    /// <summary>
    /// Runs the download and swallows the failure to start ffmpeg, which is expected here.
    /// </summary>
    private async Task RunIgnoringTheMissingFfmpegAsync(DownloadJob job)
    {
        try
        {
            await _downloader.RunAsync(job, _config, CancellationToken.None);
        }
        catch (Exception)
        {
            // There is no ffmpeg at the fake encoder's path. Everything these tests assert has
            // already happened by the time it fails to start.
        }
    }

    private sealed class CountingProbe : ISourceProbe
    {
        public int Calls { get; private set; }

        public SourceInfo Result { get; set; } = new(null, null, IsHls: false);

        public Task<SourceInfo> ProbeSourceAsync(string url, PluginConfiguration config, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
