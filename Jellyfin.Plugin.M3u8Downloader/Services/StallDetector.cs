using System;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Watches a download's reported position and decides when it has stopped making progress.
/// </summary>
/// <remarks>
/// Split out of <see cref="FfmpegDownloader"/> and driven entirely by values passed in -- including
/// the clock -- for the same reason the argument builders are: the interesting behaviour is the
/// decision, and it should be assertable without spawning a process or waiting real minutes.
///
/// The clock is reset by the position <em>advancing</em>, not by output arriving. A host that keeps
/// reconnecting without ever muxing a packet still produces <c>speed=0x</c> progress lines, so
/// treating any line as a sign of life would miss exactly the case this exists to catch.
/// </remarks>
public sealed class StallDetector
{
    private readonly object _lock = new();
    private readonly TimeSpan _timeout;

    private double _lastPosition = double.NegativeInfinity;
    private DateTime _lastAdvanceUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="StallDetector"/> class.
    /// </summary>
    /// <param name="timeout">
    /// How long the position may stand still before the download counts as stalled. Zero or less
    /// disables the detector entirely.
    /// </param>
    /// <param name="startedUtc">
    /// When the download began. The window runs from here until the first advance, so a job that
    /// never produces a single frame is caught as well as one that dies partway.
    /// </param>
    public StallDetector(TimeSpan timeout, DateTime startedUtc)
    {
        _timeout = timeout;
        _lastAdvanceUtc = startedUtc;
    }

    /// <summary>
    /// Gets a value indicating whether this detector will ever report a stall.
    /// </summary>
    public bool IsEnabled => _timeout > TimeSpan.Zero;

    /// <summary>
    /// Records a position reported by ffmpeg.
    /// </summary>
    /// <param name="positionSeconds">The position, in seconds into the output.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <remarks>
    /// Called from the stdout pump while <see cref="IsStalled"/> is called from the wait loop, so
    /// this is locked rather than left to torn reads of a <see cref="double"/> pair.
    /// </remarks>
    public void Observe(double positionSeconds, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (positionSeconds <= _lastPosition)
            {
                return;
            }

            _lastPosition = positionSeconds;
            _lastAdvanceUtc = nowUtc;
        }
    }

    /// <summary>
    /// Determines whether the download has made no progress for the whole window.
    /// </summary>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><c>true</c> when the download should be killed.</returns>
    public bool IsStalled(DateTime nowUtc)
    {
        if (!IsEnabled)
        {
            return false;
        }

        lock (_lock)
        {
            return nowUtc - _lastAdvanceUtc >= _timeout;
        }
    }
}
