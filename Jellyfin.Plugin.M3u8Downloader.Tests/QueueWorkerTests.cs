using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Covers the worker's only real job: turning every way a download can end into queue state.
/// </summary>
/// <remarks>
/// Run against a real <see cref="DownloadQueueService"/> on a temporary directory, with the
/// download itself faked. The distinctions that matter here -- a shutdown that must not burn a
/// retry, a misconfiguration that must not be retried at all -- are only visible through the queue.
/// </remarks>
public sealed class QueueWorkerTests : IDisposable
{
    private readonly string _directory;
    private readonly DownloadQueueService _queue;
    private readonly FakeDownloader _downloader = new();
    private readonly FakeLibraryMonitor _libraryMonitor = new();
    private readonly PluginConfiguration _config;

    public QueueWorkerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "m3u8-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _queue = new DownloadQueueService(
            NullLogger<DownloadQueueService>.Instance,
            Path.Combine(_directory, "queue.json"));

        _config = new PluginConfiguration
        {
            OutputDirectory = _directory,
            MaxRetries = 3,
            RetryDelaySeconds = 600,

            // Every job here is on one host, so the default courtesy gap would put a real
            // five-second wall between them. The gap has its own test below.
            DelayBetweenDownloadsSeconds = 0,
        };
    }

    public void Dispose()
    {
        _queue.Dispose();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task Success_MarksTheJobCompletedAndNotifiesTheLibrary()
    {
        var expected = Path.Combine(_directory, "show.mkv");
        _downloader.OnRun = (_, _) => Task.FromResult(expected);

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("show") });
            await WaitForStatusAsync(JobStatus.Completed);
        }

        Assert.Equal(expected, Single().ResolvedPath);
        Assert.Contains(expected, _libraryMonitor.Reported);
    }

    [Fact]
    public async Task ScanLibraryAfterDownload_Off_DoesNotNotifyTheLibrary()
    {
        _config.ScanLibraryAfterDownload = false;
        _downloader.OnRun = (_, _) => Task.FromResult(Path.Combine(_directory, "show.mkv"));

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("show") });
            await WaitForStatusAsync(JobStatus.Completed);
        }

        Assert.Empty(_libraryMonitor.Reported);
    }

    [Fact]
    public async Task TransientFailure_IsRequeuedWithBackoff()
    {
        _downloader.OnRun = (_, _) => throw new InvalidOperationException("ffmpeg exited with code 1");

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("flaky") });

            // Waiting on the status would pass instantly: the job starts out Queued and is put
            // back the same way. The attempt counter is what says the failure was recorded.
            await WaitForAsync(() => Single().Attempts == 1);
            Assert.Equal(JobStatus.Queued, Single().Status);
        }

        var job = Single();
        Assert.Equal(1, job.Attempts);
        Assert.NotNull(job.NotBeforeUtc);
        Assert.Contains("code 1", job.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Misconfiguration_FailsImmediatelyWithoutBurningRetries()
    {
        // A rejected filename fails identically every time, so retrying only delays the report.
        _downloader.OnRun = (_, _) => throw new ArgumentException("nope", "requestedFileName");

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("doomed") });
            await WaitForStatusAsync(JobStatus.Failed);
        }

        Assert.Equal(1, Single().Attempts);
    }

    [Fact]
    public async Task Shutdown_LeavesTheJobRunnableAndDoesNotBurnARetry()
    {
        // The process tree usually dies before the shutdown token is observed, so ffmpeg reports a
        // non-zero exit. Treating that as a failure would burn a retry on every restart.
        _downloader.OnRun = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("interrupted") });
            await WaitForStatusAsync(JobStatus.Downloading);
        }

        var job = Single();
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.LastError);

        // Still Downloading on disk, which is what the queue resets to Queued on the next boot.
        Assert.Equal(JobStatus.Downloading, job.Status);
    }

    [Fact]
    public async Task QueuePaused_ClaimsNothing()
    {
        _config.QueuePaused = true;
        _downloader.OnRun = (_, _) => Task.FromResult(Path.Combine(_directory, "show.mkv"));

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("waiting") });
            await Task.Delay(250);

            Assert.Equal(JobStatus.Queued, Single().Status);

            // ...and resuming picks it up without anything else prodding the queue.
            _config.QueuePaused = false;
            await WaitForStatusAsync(JobStatus.Completed);
        }
    }

    [Fact]
    public async Task MaxConcurrentDownloads_BoundsHowManyRunAtOnce()
    {
        _config.MaxConcurrentDownloads = 2;

        // This test is about the global limit. Every job is on one host, so the per-host cap would
        // otherwise be the binding constraint and hide what is being asserted.
        _config.MaxConcurrentPerHost = 2;

        using var release = new SemaphoreSlim(0);
        var started = 0;
        _downloader.OnRun = async (job, _) =>
        {
            Interlocked.Increment(ref started);
            await release.WaitAsync();
            return Path.Combine(_directory, job.RequestedFileName + ".mkv");
        };

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("a"), Job("b"), Job("c"), Job("d") });

            await WaitForAsync(() => Volatile.Read(ref started) == 2);
            await Task.Delay(250);
            Assert.Equal(2, Volatile.Read(ref started));

            release.Release(4);
            await WaitForAsync(() => _queue.GetAll().All(j => j.Status == JobStatus.Completed));
        }
    }

    [Fact]
    public async Task MaxConcurrentPerHost_BoundsHowManyRunAgainstOneSite()
    {
        // The limit that actually prevents an IP block: several downloads at once is fine spread
        // across sites, and is what gets you blocked when aimed at one.
        _config.MaxConcurrentDownloads = 4;
        _config.MaxConcurrentPerHost = 1;

        using var release = new SemaphoreSlim(0);
        var started = 0;
        _downloader.OnRun = async (job, _) =>
        {
            Interlocked.Increment(ref started);
            await release.WaitAsync();
            return Path.Combine(_directory, job.RequestedFileName + ".mkv");
        };

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("a"), Job("b"), Job("c") });

            await WaitForAsync(() => Volatile.Read(ref started) == 1);
            await Task.Delay(250);
            Assert.Equal(1, Volatile.Read(ref started));

            release.Release(3);
            await WaitForAsync(() => _queue.GetAll().All(j => j.Status == JobStatus.Completed));
        }
    }

    [Fact]
    public async Task RateLimited_PausesTheWholeHostRatherThanRetryingSoon()
    {
        // A remux cannot resume, so the ordinary retry re-requests the whole stream while the host
        // is still refusing us -- which is how a temporary throttle becomes a lasting block. The
        // pause has to cover the episodes queued behind it, not just the job that tripped it.
        _config.RetryDelaySeconds = 1;
        _config.RateLimitBackoffMinutes = 30;

        var started = 0;
        _downloader.OnRun = (_, _) =>
        {
            Interlocked.Increment(ref started);
            throw new RateLimitedException("ffmpeg exited with code 1: HTTP error 429 Too Many Requests");
        };

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("first"), Job("second") });

            await WaitForAsync(() => _queue.GetAll().Any(j => j.Attempts == 1));

            // Long enough that the one-second retry delay would have fired, and that the second
            // job would have been claimed had the pause applied only to the first.
            await Task.Delay(500);
            Assert.Equal(1, Volatile.Read(ref started));
        }

        var failed = _queue.GetAll().Single(j => j.Attempts == 1);
        Assert.Equal(JobStatus.Queued, failed.Status);
        Assert.NotNull(failed.NotBeforeUtc);

        // The backoff, not RetryDelaySeconds.
        Assert.True(failed.NotBeforeUtc > DateTime.UtcNow.AddMinutes(20));
    }

    [Fact]
    public async Task OrdinaryFailure_StillUsesTheShortRetryDelay()
    {
        // The counterpart to the test above: only a rate limit earns the long pause.
        _config.RetryDelaySeconds = 30;
        _config.RateLimitBackoffMinutes = 30;

        _downloader.OnRun = (_, _) => throw new InvalidOperationException("ffmpeg exited with code 1: Error muxing a packet");

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("flaky") });
            await WaitForAsync(() => Single().Attempts == 1);
        }

        Assert.True(Single().NotBeforeUtc < DateTime.UtcNow.AddMinutes(20));
    }

    [Fact]
    public async Task DelayBetweenDownloads_SpacesConsecutiveJobsOnOneHost()
    {
        // A queue of episodes from one site must not arrive as one unbroken stream of requests.
        _config.DelayBetweenDownloadsSeconds = 30;

        var started = 0;
        _downloader.OnRun = (job, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult(Path.Combine(_directory, job.RequestedFileName + ".mkv"));
        };

        await using (await StartAsync())
        {
            _queue.AddRange(new[] { Job("one"), Job("two") });

            await WaitForAsync(() => Volatile.Read(ref started) == 1);
            await Task.Delay(500);

            Assert.Equal(1, Volatile.Read(ref started));
        }
    }

    [Fact]
    public async Task Startup_SweepsPartFilesLeftByAnUncleanShutdown()
    {
        // A .part file is the name reservation, so one left behind by a kill -9 would push every
        // later download of that title to "Title (2).mkv" forever.
        var orphan = Path.Combine(_directory, "abandoned.mkv.part");
        var nested = Path.Combine(_directory, "Show", "Episode.mkv.part");
        var keep = Path.Combine(_directory, "finished.mkv");

        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(orphan, string.Empty);
        File.WriteAllText(nested, string.Empty);
        File.WriteAllText(keep, string.Empty);

        await using (await StartAsync())
        {
            await WaitForAsync(() => !File.Exists(orphan) && !File.Exists(nested));
        }

        Assert.True(File.Exists(keep), "a finished download must not be swept");
    }

    private DownloadJob Single() => _queue.GetAll().Single();

    private static DownloadJob Job(string name) => new()
    {
        Url = "https://example.com/" + name + ".m3u8",
        RequestedFileName = name,
    };

    private async Task<WorkerHandle> StartAsync()
    {
        var worker = new QueueWorker(
            _queue,
            () => _config,
            _downloader,
            _libraryMonitor,
            new FakeMediaEncoder(),
            NullLogger<QueueWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        return new WorkerHandle(worker);
    }

    private Task WaitForStatusAsync(JobStatus status) =>
        WaitForAsync(() => _queue.GetAll() is [var job] && job.Status == status);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the worker to reach the expected state.");
    }

    /// <summary>Stops the worker at the end of a block, the way the host would.</summary>
    private sealed class WorkerHandle : IAsyncDisposable
    {
        private readonly QueueWorker _worker;

        public WorkerHandle(QueueWorker worker) => _worker = worker;

        public async ValueTask DisposeAsync()
        {
            await _worker.StopAsync(CancellationToken.None);
            _worker.Dispose();
        }
    }

    private sealed class FakeDownloader : IFfmpegDownloader
    {
        public Func<DownloadJob, CancellationToken, Task<string>> OnRun { get; set; } =
            (_, _) => Task.FromResult(string.Empty);

        public Task<string> RunAsync(DownloadJob job, PluginConfiguration config, CancellationToken cancellationToken) =>
            OnRun(job, cancellationToken);
    }

    private sealed class FakeLibraryMonitor : MediaBrowser.Controller.Library.ILibraryMonitor
    {
        public ConcurrentBag<string> Reported { get; } = new();

        public void ReportFileSystemChanged(string path) => Reported.Add(path);

        public void ReportFileSystemChangeBeginning(string path)
        {
        }

        public void ReportFileSystemChangeComplete(string path, bool refreshPath)
        {
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }
    }
}
