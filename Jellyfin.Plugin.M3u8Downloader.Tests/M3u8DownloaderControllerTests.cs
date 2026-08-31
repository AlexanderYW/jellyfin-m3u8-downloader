using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Api;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model;
using Jellyfin.Plugin.M3u8Downloader.Model.Dto;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

/// <summary>
/// Covers what the dashboard page actually depends on: which status code each action returns, and
/// that a partly valid paste comes back with both halves rather than being rejected wholesale.
/// </summary>
public sealed class M3u8DownloaderControllerTests : IDisposable
{
    private readonly string _directory;
    private readonly DownloadQueueService _queue;
    private readonly PluginConfiguration _config;
    private readonly M3u8DownloaderController _controller;

    public M3u8DownloaderControllerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "m3u8-controller-tests", Guid.NewGuid().ToString("N"));

        _queue = new DownloadQueueService(
            NullLogger<DownloadQueueService>.Instance,
            Path.Combine(_directory, "queue.json"));

        _config = new PluginConfiguration { OutputDirectory = Path.Combine(_directory, "out") };
        _controller = new M3u8DownloaderController(_queue, () => _config);
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
    public void AddJobs_PartlyValidPaste_QueuesTheGoodLinesAndReportsTheRest()
    {
        var result = AddJobs("https://example.com/a.m3u8|Show A\nnot-a-url\nftp://example.com/b.m3u8");

        Assert.Single(result.Added);
        Assert.Equal("Show A", result.Added[0].FileName);

        Assert.Equal(2, result.Rejected.Count);
        Assert.Equal(2, result.Rejected[0].LineNumber);
        Assert.Equal(3, result.Rejected[1].LineNumber);
        Assert.Contains("ftp", result.Rejected[1].Error, StringComparison.Ordinal);

        Assert.Single(_queue.GetAll());
    }

    [Fact]
    public void AddJobs_NoOutputDirectoryConfigured_RejectsInlineRatherThanQueueingDoomedJobs()
    {
        // Otherwise every line becomes a job that fails identically on every retry.
        _config.OutputDirectory = string.Empty;

        var result = AddJobs("https://example.com/a.m3u8");

        Assert.Empty(result.Added);
        Assert.Single(result.Rejected);
        Assert.Empty(_queue.GetAll());
    }

    [Fact]
    public void AddJobs_AppliesTheDestinationFolderToEveryLine()
    {
        var result = AddJobs("https://example.com/a.m3u8|One\nhttps://example.com/b.m3u8|Two", "Show/Season 01");

        Assert.All(result.Added, job => Assert.StartsWith("Show/Season 01/", job.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void GetJobs_ReturnsTheQueue()
    {
        AddJobs("https://example.com/a.m3u8|Show A");

        var jobs = Assert.IsType<OkObjectResult>(_controller.GetJobs().Result).Value;

        Assert.Single(Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<JobDto>>(jobs));
    }

    [Fact]
    public void GetJobs_CompletedJob_CarriesWhereTheFileActuallyLanded()
    {
        // The download can be published under a different name than the one reserved for it, so
        // the dashboard needs the real path rather than inferring it from the requested one.
        var id = QueueOne();
        _queue.TryDequeueNext(1);
        var landed = Path.Combine(_config.OutputDirectory, "Show A (2).mkv");
        _queue.MarkCompleted(id, landed);

        var jobs = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<JobDto>>(
            Assert.IsType<OkObjectResult>(_controller.GetJobs().Result).Value);

        Assert.Equal(landed, Assert.Single(jobs).ResolvedPath);
    }

    [Fact]
    public void GetJobs_UnfinishedJob_HasNoResolvedPath()
    {
        QueueOne();

        var jobs = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<JobDto>>(
            Assert.IsType<OkObjectResult>(_controller.GetJobs().Result).Value);

        Assert.Null(Assert.Single(jobs).ResolvedPath);
    }

    [Fact]
    public void DeleteJob_KnownJob_IsNoContentAndUnknownJobIsNotFound()
    {
        var id = QueueOne();

        Assert.IsType<NoContentResult>(_controller.DeleteJob(id));
        Assert.IsType<NotFoundResult>(_controller.DeleteJob(Guid.NewGuid()));
    }

    [Fact]
    public void RetryJob_OnlyAppliesToAFinishedJob()
    {
        var id = QueueOne();

        // Still queued, so there is nothing to retry.
        Assert.IsType<NotFoundResult>(_controller.RetryJob(id));

        _queue.TryDequeueNext(1);
        _queue.MarkAttemptFailed(id, "boom", 0, TimeSpan.Zero);

        Assert.IsType<NoContentResult>(_controller.RetryJob(id));
        Assert.IsType<NotFoundResult>(_controller.RetryJob(Guid.NewGuid()));
    }

    [Fact]
    public void MoveJobToTop_AndToBottom_OnlyApplyToQueuedJobs()
    {
        AddJobs("https://example.com/a.m3u8|A\nhttps://example.com/b.m3u8|B");
        var first = _queue.GetAll().Single(j => j.RequestedFileName == "A").Id;

        Assert.IsType<NoContentResult>(_controller.MoveJobToBottom(first));
        Assert.IsType<NoContentResult>(_controller.MoveJobToTop(first));

        _queue.TryDequeueNext(1);
        Assert.IsType<NotFoundResult>(_controller.MoveJobToTop(first));
        Assert.IsType<NotFoundResult>(_controller.MoveJobToBottom(Guid.NewGuid()));
    }

    [Fact]
    public void ClearFinished_ReturnsHowManyWereRemoved()
    {
        var id = QueueOne();
        _queue.TryDequeueNext(1);
        _queue.MarkCompleted(id, "/tmp/a.mkv");

        Assert.Equal(1, Assert.IsType<OkObjectResult>(_controller.ClearFinished().Result).Value);
        Assert.Empty(_queue.GetAll());
    }

    private AddJobsResult AddJobs(string text, string? folder = null)
    {
        var request = new AddJobsRequest { Text = text, Folder = folder };
        var result = _controller.AddJobs(request);

        return Assert.IsType<AddJobsResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private Guid QueueOne()
    {
        AddJobs("https://example.com/a.m3u8|Show A");
        return _queue.GetAll().Single().Id;
    }
}
