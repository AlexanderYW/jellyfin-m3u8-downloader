using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class OutputFilePublisherTests
{
    /// <summary>An absolute root, so the resolver's containment check has something to work with.</summary>
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "m3u8-publish-tests"));

    // ---------------------------------------------------------------- publishing

    [Fact]
    public void Publish_FreeName_MovesStraightOntoIt()
    {
        var moves = new List<(string From, string To)>();

        var landed = OutputFilePublisher.Publish(
            Root,
            "Show",
            Path.Combine(Root, "Show.mkv.part"),
            Path.Combine(Root, "Show.mkv"),
            _ => false,
            (from, to) => moves.Add((from, to)));

        Assert.Equal(Path.Combine(Root, "Show.mkv"), landed);
        Assert.Single(moves);
    }

    [Fact]
    public void Publish_NameTakenDuringTheDownload_ResolvesAFreshOneInsteadOfFailing()
    {
        // Nothing stops a person creating the file during a multi-hour download. Failing here would
        // throw the finished download away and start it over.
        var taken = Path.Combine(Root, "Show.mkv");
        var tempPath = Path.Combine(Root, "Show.mkv.part");
        var existing = new HashSet<string>(StringComparer.Ordinal) { taken };

        var landed = OutputFilePublisher.Publish(
            Root,
            "Show",
            tempPath,
            taken,
            existing.Contains,
            (_, to) =>
            {
                if (existing.Contains(to))
                {
                    throw new IOException("destination exists");
                }
            });

        Assert.Equal(Path.Combine(Root, "Show (2).mkv"), landed);
    }

    [Fact]
    public void Publish_NonCollisionIoError_PropagatesWithoutRetrying()
    {
        // The retry exists for a name that was taken mid-download. Anything else -- a permissions
        // change, a directory sitting where the file should go -- is not going to be fixed by
        // picking a different name, and the caller must see it so the finished .part is kept.
        var attempts = 0;

        Assert.Throws<IOException>(() => OutputFilePublisher.Publish(
            Root,
            "Show",
            Path.Combine(Root, "Show.mkv.part"),
            Path.Combine(Root, "Show.mkv"),
            _ => false,
            (_, _) =>
            {
                attempts++;
                throw new IOException("permission denied");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Publish_CollisionThatNeverClears_GivesUpRatherThanLooping()
    {
        // Bounded so a destination that is taken the instant it is resolved cannot spin forever.
        // The caller leaves the finished .part alone on the way out.
        var tempPath = Path.Combine(Root, "Show.mkv.part");
        var taken = new HashSet<string>(StringComparer.Ordinal) { Path.Combine(Root, "Show.mkv") };
        var attempts = 0;

        Assert.Throws<IOException>(() => OutputFilePublisher.Publish(
            Root,
            "Show",
            tempPath,
            Path.Combine(Root, "Show.mkv"),
            taken.Contains,
            (_, to) =>
            {
                attempts++;

                // Something claims each freshly resolved name before the move lands on it.
                taken.Add(to);
                throw new IOException("destination exists");
            }));

        Assert.Equal(5, attempts);
    }

    [Fact]
    public void Publish_DoesNotTreatItsOwnPartFileAsACollision()
    {
        // The .part being published is on disk by definition; counting it as taken would push every
        // publish one name along.
        var tempPath = Path.Combine(Root, "Show.mkv.part");
        var existing = new HashSet<string>(StringComparer.Ordinal) { Path.Combine(Root, "Show.mkv"), tempPath };

        var landed = OutputFilePublisher.Publish(
            Root,
            "Show",
            tempPath,
            Path.Combine(Root, "Show.mkv"),
            existing.Contains,
            (_, to) =>
            {
                if (existing.Contains(to))
                {
                    throw new IOException("destination exists");
                }
            });

        Assert.Equal(Path.Combine(Root, "Show (2).mkv"), landed);
    }
}
