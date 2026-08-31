using System;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

/// <summary>
/// Covers the rule that separates a slow download from a dead one: the clock is reset by the
/// position advancing, never by output merely arriving.
/// </summary>
public sealed class StallDetectorTests
{
    private static readonly DateTime _start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(10);

    [Fact]
    public void IsStalled_BeforeTheWindowElapses_IsFalse()
    {
        var detector = new StallDetector(_window, _start);

        Assert.False(detector.IsStalled(_start.AddMinutes(9)));
    }

    [Fact]
    public void IsStalled_NoProgressSinceTheStart_TripsWithoutEverObserving()
    {
        // A source that never muxes a single frame has to be caught too, not just one that dies
        // partway: the window runs from the start until the first advance.
        var detector = new StallDetector(_window, _start);

        Assert.True(detector.IsStalled(_start.AddMinutes(10)));
    }

    [Fact]
    public void Observe_AdvancingPosition_ResetsTheClock()
    {
        var detector = new StallDetector(_window, _start);

        detector.Observe(30, _start.AddMinutes(9));

        Assert.False(detector.IsStalled(_start.AddMinutes(18)));
        Assert.True(detector.IsStalled(_start.AddMinutes(19)));
    }

    [Fact]
    public void Observe_RepeatedIdenticalPosition_DoesNotResetTheClock()
    {
        // ffmpeg keeps emitting "speed=0x" lines while it reconnects to a host that has gone
        // silent, and the running position is carried forward onto each of them. Treating those as
        // a sign of life would miss exactly the case this exists to catch.
        var detector = new StallDetector(_window, _start);

        detector.Observe(30, _start.AddMinutes(1));
        detector.Observe(30, _start.AddMinutes(5));
        detector.Observe(30, _start.AddMinutes(10));

        Assert.True(detector.IsStalled(_start.AddMinutes(11)));
    }

    [Fact]
    public void Observe_PositionGoingBackwards_DoesNotResetTheClock()
    {
        var detector = new StallDetector(_window, _start);

        detector.Observe(30, _start.AddMinutes(1));
        detector.Observe(20, _start.AddMinutes(9));

        Assert.True(detector.IsStalled(_start.AddMinutes(11)));
    }

    [Fact]
    public void IsStalled_ZeroTimeout_IsAlwaysFalse()
    {
        var detector = new StallDetector(TimeSpan.Zero, _start);

        Assert.False(detector.IsEnabled);
        Assert.False(detector.IsStalled(_start.AddDays(7)));
    }
}
