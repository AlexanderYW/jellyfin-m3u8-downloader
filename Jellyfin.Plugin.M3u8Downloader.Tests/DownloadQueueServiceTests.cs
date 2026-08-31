using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.M3u8Downloader.Model;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

/// <summary>
/// Covers the queue's state machine and its persistence, against a real state file in a temporary
/// directory. These paths are the ones a server restart or a cancelled download actually exercise,
/// so they are tested through the same public surface the worker and the API use.
/// </summary>
public sealed class DownloadQueueServiceTests : IDisposable
{
    private const int Unlimited = 99;

    private readonly string _directory;
    private readonly string _statePath;

    public DownloadQueueServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "m3u8-queue-tests", Guid.NewGuid().ToString("N"));
        _statePath = Path.Combine(_directory, "queue.json");
    }

    public void Dispose()
    {
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

    private DownloadQueueService NewQueue() =>
        new(NullLogger<DownloadQueueService>.Instance, _statePath);

    private static DownloadJob Job(string name, DateTime? created = null) => new()
    {
        Url = "https://example.com/" + name + ".m3u8",
        RequestedFileName = name,
        CreatedUtc = created ?? DateTime.UtcNow,
    };

    // ---------------------------------------------------------------- claiming

    [Fact]
    public void TryDequeueNext_ClaimsJobsInTheOrderTheyWereAdded()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("first"), Job("second") });

        var claimed = queue.TryDequeueNext(Unlimited);

        Assert.NotNull(claimed);
        Assert.Equal("first", claimed!.RequestedFileName);
        Assert.Equal(JobStatus.Downloading, claimed.Status);
        Assert.NotNull(claimed.StartedUtc);
    }

    [Fact]
    public void TryDequeueNext_AtTheConcurrencyLimit_ClaimsNothingMore()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("a"), Job("b"), Job("c") });

        Assert.NotNull(queue.TryDequeueNext(2));
        Assert.NotNull(queue.TryDequeueNext(2));
        Assert.Null(queue.TryDequeueNext(2));
    }

    [Fact]
    public void TryDequeueNext_LimitOfOne_IsTheOldSequentialBehaviour()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("a"), Job("b") });

        Assert.NotNull(queue.TryDequeueNext(1));
        Assert.Null(queue.TryDequeueNext(1));
    }

    [Fact]
    public void TryDequeueNext_SkipsAJobStillInItsRetryBackoff()
    {
        using var queue = NewQueue();
        var waiting = Job("waiting");
        waiting.NotBeforeUtc = DateTime.UtcNow.AddMinutes(5);
        queue.AddRange(new[] { waiting });

        Assert.Null(queue.TryDequeueNext(Unlimited));
    }

    [Fact]
    public void TryDequeueNext_ClaimsAJobWhoseBackoffHasPassed()
    {
        using var queue = NewQueue();
        var ready = Job("ready");
        ready.NotBeforeUtc = DateTime.UtcNow.AddMinutes(-1);
        queue.AddRange(new[] { ready });

        var claimed = queue.TryDequeueNext(Unlimited);

        Assert.NotNull(claimed);

        // Cleared on claim so a later failure computes a fresh backoff instead of inheriting one.
        Assert.Null(claimed!.NotBeforeUtc);
    }

    [Fact]
    public void TryDequeueNext_EmptyQueue_ReturnsNull()
    {
        using var queue = NewQueue();

        Assert.Null(queue.TryDequeueNext(Unlimited));
    }

    // ---------------------------------------------------------------- transitions

    [Fact]
    public void MarkCompleted_SnapsProgressToTheFullDuration()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("done") });
        var job = queue.TryDequeueNext(Unlimited)!;
        queue.ReportProgress(job.Id, 118.4, 120);

        queue.MarkCompleted(job.Id, "/media/done.mkv");

        var stored = queue.GetAll().Single();
        Assert.Equal(JobStatus.Completed, stored.Status);
        Assert.Equal("/media/done.mkv", stored.ResolvedPath);
        Assert.Equal(120, stored.PositionSeconds);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public void MarkAttemptFailed_RequeuesWithBackoffWhileRetriesRemain()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("flaky") });
        var job = queue.TryDequeueNext(Unlimited)!;

        var requeued = queue.MarkAttemptFailed(job.Id, "boom", maxRetries: 2, TimeSpan.FromSeconds(30));

        Assert.True(requeued);
        var stored = queue.GetAll().Single();
        Assert.Equal(JobStatus.Queued, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal("boom", stored.LastError);
        Assert.NotNull(stored.NotBeforeUtc);
        Assert.Null(stored.StartedUtc);
    }

    [Fact]
    public void MarkAttemptFailed_PastTheRetryBudget_FailsPermanently()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("doomed") });
        var id = queue.TryDequeueNext(Unlimited)!.Id;

        Assert.True(queue.MarkAttemptFailed(id, "one", maxRetries: 1, TimeSpan.Zero));
        queue.TryDequeueNext(Unlimited);
        Assert.False(queue.MarkAttemptFailed(id, "two", maxRetries: 1, TimeSpan.Zero));

        var stored = queue.GetAll().Single();
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal("two", stored.LastError);
        Assert.NotNull(stored.CompletedUtc);
    }

    [Fact]
    public void MarkAttemptFailed_WithNoRetryBudget_FailsOnTheFirstAttempt()
    {
        // The path the worker takes for an error that cannot succeed on a retry, such as a
        // filename the output resolver rejects.
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("invalid") });
        var id = queue.TryDequeueNext(Unlimited)!.Id;

        Assert.False(queue.MarkAttemptFailed(id, "bad name", maxRetries: 0, TimeSpan.Zero));
        Assert.Equal(JobStatus.Failed, queue.GetAll().Single().Status);
    }

    [Fact]
    public void Retry_ResetsAFailedJobAndRefusesARunningOne()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("retryable") });
        var id = queue.TryDequeueNext(Unlimited)!.Id;

        Assert.False(queue.Retry(id));

        queue.MarkAttemptFailed(id, "boom", maxRetries: 0, TimeSpan.Zero);
        Assert.True(queue.Retry(id));

        var stored = queue.GetAll().Single();
        Assert.Equal(JobStatus.Queued, stored.Status);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
        Assert.Null(stored.CompletedUtc);
    }

    [Fact]
    public void CancelOrRemove_RemovesAPendingJobButLeavesARunningOneToTheWorker()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("pending"), Job("running") });
        var running = queue.TryDequeueNext(Unlimited)!;

        Assert.True(queue.CancelOrRemove(running.Id));

        // Still present: the worker records the cancellation once ffmpeg has actually stopped.
        Assert.Contains(queue.GetAll(), j => j.Id == running.Id);

        var pending = queue.GetAll().Single(j => j.Id != running.Id);
        Assert.True(queue.CancelOrRemove(pending.Id));
        Assert.DoesNotContain(queue.GetAll(), j => j.Id == pending.Id);
    }

    [Fact]
    public void CancelOrRemove_UnknownJob_ReturnsFalse()
    {
        using var queue = NewQueue();

        Assert.False(queue.CancelOrRemove(Guid.NewGuid()));
    }

    [Fact]
    public void CancelOrRemove_CancelsOnlyTheNamedJob()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("one"), Job("two") });

        var first = queue.TryDequeueNext(2)!;
        var second = queue.TryDequeueNext(2)!;

        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        queue.SetActiveCancellation(first.Id, firstCts);
        queue.SetActiveCancellation(second.Id, secondCts);

        queue.CancelOrRemove(first.Id);

        Assert.True(firstCts.IsCancellationRequested);
        Assert.False(secondCts.IsCancellationRequested);
    }

    [Fact]
    public void ClearActiveCancellation_RemovesOnlyThatJobsRegistration()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("one"), Job("two") });
        var first = queue.TryDequeueNext(2)!;
        var second = queue.TryDequeueNext(2)!;

        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        queue.SetActiveCancellation(first.Id, firstCts);
        queue.SetActiveCancellation(second.Id, secondCts);

        queue.ClearActiveCancellation(first.Id);

        queue.CancelOrRemove(first.Id);
        Assert.False(firstCts.IsCancellationRequested);

        queue.CancelOrRemove(second.Id);
        Assert.True(secondCts.IsCancellationRequested);
    }

    [Fact]
    public void CancelOrRemove_BeforeTheWorkerRegisters_StillCancelsTheJob()
    {
        // A job is Downloading the moment it is claimed, but the worker only registers its
        // cancellation source once it starts running it. A cancel arriving in between used to
        // report success and let the download run to completion.
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("racing") });
        var claimed = queue.TryDequeueNext(Unlimited)!;

        Assert.True(queue.CancelOrRemove(claimed.Id));

        using var cts = new CancellationTokenSource();
        queue.SetActiveCancellation(claimed.Id, cts);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void CancelOrRemove_ParkedRequest_DoesNotCarryIntoALaterAttempt()
    {
        // The id survives a retry, so a request left over from an attempt that already ended must
        // not cancel the next one.
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("racing") });
        var claimed = queue.TryDequeueNext(Unlimited)!;

        queue.CancelOrRemove(claimed.Id);
        queue.ClearActiveCancellation(claimed.Id);

        using var cts = new CancellationTokenSource();
        queue.SetActiveCancellation(claimed.Id, cts);

        Assert.False(cts.IsCancellationRequested);
    }

    // ---------------------------------------------------------------- ordering

    [Fact]
    public void MoveToTop_MakesAJobTheNextOneClaimed()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("first"), Job("second"), Job("third") });
        var third = queue.GetAll().Single(j => j.RequestedFileName == "third");

        Assert.True(queue.MoveToTop(third.Id));

        Assert.Equal("third", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
    }

    [Fact]
    public void MoveToTop_RefusesJobsThatAreNotQueued()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("running"), Job("waiting") });
        var running = queue.TryDequeueNext(Unlimited)!;

        Assert.False(queue.MoveToTop(running.Id));
        Assert.False(queue.MoveToTop(Guid.NewGuid()));
    }

    [Fact]
    public void MoveToTop_SurvivesARestart()
    {
        Guid movedId;

        using (var queue = NewQueue())
        {
            queue.AddRange(new[] { Job("first"), Job("second"), Job("third") });
            movedId = queue.GetAll().Single(j => j.RequestedFileName == "third").Id;
            queue.MoveToTop(movedId);
        }

        using var reopened = NewQueue();

        Assert.Equal(movedId, reopened.TryDequeueNext(Unlimited)!.Id);
    }

    [Fact]
    public void MoveToBottom_MakesAJobTheLastOneClaimed()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("first"), Job("second"), Job("third") });
        var first = queue.GetAll().Single(j => j.RequestedFileName == "first");

        Assert.True(queue.MoveToBottom(first.Id));

        Assert.Equal("second", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
        Assert.Equal("third", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
        Assert.Equal("first", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
    }

    [Fact]
    public void MoveToBottom_StaysBehindJobsAddedAfterwards()
    {
        // The counter has to follow the moved job, or the next job added lands in front of it.
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("first"), Job("second") });
        var first = queue.GetAll().Single(j => j.RequestedFileName == "first");

        queue.MoveToBottom(first.Id);
        queue.AddRange(new[] { Job("third") });

        Assert.Equal("second", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
        Assert.Equal("first", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
        Assert.Equal("third", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
    }

    [Fact]
    public void MoveToBottom_RefusesJobsThatAreNotQueued()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("running"), Job("waiting") });
        var running = queue.TryDequeueNext(Unlimited)!;

        Assert.False(queue.MoveToBottom(running.Id));
        Assert.False(queue.MoveToBottom(Guid.NewGuid()));
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public void ClearFinished_RemovesOnlyTerminalJobs()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("done"), Job("waiting") });
        var done = queue.TryDequeueNext(Unlimited)!;
        queue.MarkCompleted(done.Id, "/media/done.mkv");

        Assert.Equal(1, queue.ClearFinished());
        Assert.Equal("waiting", queue.GetAll().Single().RequestedFileName);
    }

    [Fact]
    public void PruneHistory_DropsOldFinishedJobsAndKeepsRecentOnes()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("old"), Job("recent") });

        var old = queue.TryDequeueNext(Unlimited)!;
        queue.MarkCompleted(old.Id, "/media/old.mkv");
        old.CompletedUtc = DateTime.UtcNow.AddDays(-10);

        var recent = queue.TryDequeueNext(Unlimited)!;
        queue.MarkCompleted(recent.Id, "/media/recent.mkv");

        Assert.Equal(1, queue.PruneHistory(7));
        Assert.Equal("recent", queue.GetAll().Single().RequestedFileName);
    }

    [Fact]
    public void PruneHistory_LeavesUnfinishedJobsAlone()
    {
        using var queue = NewQueue();
        var stale = Job("stale", DateTime.UtcNow.AddYears(-1));
        queue.AddRange(new[] { stale });

        Assert.Equal(0, queue.PruneHistory(1));
        Assert.Single(queue.GetAll());
    }

    [Fact]
    public void PruneHistory_DisabledByDefault()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("done") });
        var done = queue.TryDequeueNext(Unlimited)!;
        queue.MarkCompleted(done.Id, "/media/done.mkv");
        done.CompletedUtc = DateTime.UtcNow.AddYears(-5);

        Assert.Equal(0, queue.PruneHistory(0));
        Assert.Single(queue.GetAll());
    }

    // ---------------------------------------------------------------- snapshots and persistence

    [Fact]
    public void GetAll_HandsBackCopiesThatCannotMutateQueueState()
    {
        using var queue = NewQueue();
        queue.AddRange(new[] { Job("job") });

        var snapshot = queue.GetAll().Single();
        snapshot.Status = JobStatus.Completed;
        snapshot.RequestedFileName = "rewritten";

        var actual = queue.GetAll().Single();
        Assert.Equal(JobStatus.Queued, actual.Status);
        Assert.Equal("job", actual.RequestedFileName);
    }

    [Fact]
    public void Queue_SurvivesARestart()
    {
        using (var queue = NewQueue())
        {
            queue.AddRange(new[] { Job("kept") });
        }

        using var reopened = NewQueue();

        var stored = reopened.GetAll().Single();
        Assert.Equal("kept", stored.RequestedFileName);
        Assert.Equal(JobStatus.Queued, stored.Status);
    }

    [Fact]
    public void Restart_ResetsAJobThatWasDownloadingWhenTheServerDied()
    {
        using (var queue = NewQueue())
        {
            queue.AddRange(new[] { Job("interrupted") });
            var job = queue.TryDequeueNext(Unlimited)!;
            queue.ReportProgress(job.Id, 42, 120);

            // Persisted as Downloading, exactly as an unclean shutdown would leave it.
            queue.MarkAttemptFailed(job.Id, "ignored", maxRetries: 5, TimeSpan.Zero);
            queue.TryDequeueNext(Unlimited);
        }

        using var reopened = NewQueue();

        var stored = reopened.GetAll().Single();
        Assert.Equal(JobStatus.Queued, stored.Status);
        Assert.Null(stored.StartedUtc);
        Assert.Equal(0, stored.PositionSeconds);
    }

    [Fact]
    public void Load_NumbersJobsWrittenBeforeQueuePositionsExisted()
    {
        // A queue.json from an older build has no QueuePosition, so every job deserialises at
        // zero. Creation order is what the old build ran them in and must be preserved.
        var older = DateTime.UtcNow.AddHours(-2);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_statePath, $$"""
        [
          { "Id": "{{Guid.NewGuid()}}", "Url": "https://example.com/b.m3u8", "RequestedFileName": "second",
            "Status": 0, "PositionSeconds": 0, "Attempts": 0, "CreatedUtc": "{{older.AddMinutes(10):O}}" },
          { "Id": "{{Guid.NewGuid()}}", "Url": "https://example.com/a.m3u8", "RequestedFileName": "first",
            "Status": 0, "PositionSeconds": 0, "Attempts": 0, "CreatedUtc": "{{older:O}}" }
        ]
        """);

        using var queue = NewQueue();

        Assert.Equal("first", queue.TryDequeueNext(Unlimited)!.RequestedFileName);
    }

    [Fact]
    public void Load_ContinuesNumberingSoNewJobsQueueBehindRestoredOnes()
    {
        using (var queue = NewQueue())
        {
            queue.AddRange(new[] { Job("restored") });
        }

        using var reopened = NewQueue();
        reopened.AddRange(new[] { Job("added-later") });

        Assert.Equal("restored", reopened.TryDequeueNext(Unlimited)!.RequestedFileName);
    }

    [Fact]
    public void Load_CorruptStateFile_StartsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_statePath, "{ this is not the queue }");

        using var queue = NewQueue();

        Assert.Empty(queue.GetAll());
    }
}
