using System;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class RetryBackoffTests
{
    private static readonly TimeSpan _base = TimeSpan.FromMinutes(30);

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void ForRateLimit_DoublesWithEachAttempt(int attempts, double expectedMinutes)
    {
        var result = RetryBackoff.ForRateLimit(_base, attempts, jitterFraction: 0, new NoJitterRandom());

        Assert.Equal(expectedMinutes, result.TotalMinutes, precision: 3);
    }

    [Fact]
    public void ForRateLimit_StopsDoublingAtTheCap()
    {
        // Past MaxDoublings the delay must not keep growing, or a long-running queue ends up
        // parked for days over a host that may well have calmed down hours ago.
        var capped = RetryBackoff.ForRateLimit(_base, 5, 0, new NoJitterRandom());
        var wayPast = RetryBackoff.ForRateLimit(_base, 50, 0, new NoJitterRandom());

        Assert.Equal(capped, wayPast);
        Assert.Equal(_base.TotalMinutes * (1 << RetryBackoff.MaxDoublings), wayPast.TotalMinutes, precision: 3);
    }

    [Fact]
    public void ForRateLimit_KeepsJitterWithinItsFraction()
    {
        // Seeded rather than mocked: the point is that every draw from a real Random lands inside
        // the band, which a fixed stub could not show.
        var random = new Random(20260901);

        for (var i = 0; i < 1000; i++)
        {
            var result = RetryBackoff.ForRateLimit(_base, attempts: 1, jitterFraction: 0.2, random);

            Assert.InRange(result.TotalMinutes, 30 * 0.8, 30 * 1.2);
        }
    }

    [Fact]
    public void ForRateLimit_SpreadsRepeatedDrawsApart()
    {
        // The whole purpose of the jitter is that jobs released together do not leave together.
        var random = new Random(7);

        var first = RetryBackoff.ForRateLimit(_base, 1, 0.2, random);
        var second = RetryBackoff.ForRateLimit(_base, 1, 0.2, random);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForRateLimit_ZeroJitterIsExact()
    {
        var result = RetryBackoff.ForRateLimit(_base, attempts: 1, jitterFraction: 0, new Random(1));

        Assert.Equal(_base, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ForRateLimit_NonPositiveBackoffStaysNonPositive(int minutes)
    {
        // A jittered zero could otherwise come out negative and set NotBeforeUtc in the past.
        var configured = TimeSpan.FromMinutes(minutes);

        var result = RetryBackoff.ForRateLimit(configured, attempts: 3, jitterFraction: 0.2, new Random(1));

        Assert.Equal(configured, result);
    }

    [Fact]
    public void ForRateLimit_NeverReachesZeroForAConfiguredBackoff()
    {
        // An absurd jitter fraction is clamped rather than allowed to cancel the backoff outright.
        var random = new Random(99);

        for (var i = 0; i < 200; i++)
        {
            var result = RetryBackoff.ForRateLimit(_base, 1, jitterFraction: 5, random);

            Assert.True(result > TimeSpan.Zero, $"backoff collapsed to {result}");
        }
    }

    /// <summary>A Random that always returns the midpoint, so jitter cancels out.</summary>
    private sealed class NoJitterRandom : Random
    {
        public override double NextDouble() => 0.5;
    }
}
