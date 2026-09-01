using System;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Works out how long to hold a host off after it refuses us.
/// </summary>
/// <remarks>
/// Pure and static, with the randomness passed in, for the same reason
/// <see cref="OutputPathResolver"/> takes an existence predicate: the interesting behaviour is the
/// schedule itself, and it should be assertable without waiting for real time to pass.
/// </remarks>
public static class RetryBackoff
{
    /// <summary>
    /// How many times the base delay may double. Four doublings turns a thirty-minute backoff into
    /// eight hours, which is long enough to outlast any rolling window a host is likely to use and
    /// short enough that an overnight queue still finishes.
    /// </summary>
    public const int MaxDoublings = 4;

    /// <summary>Default proportion by which a delay is spread either side of its nominal value.</summary>
    public const double DefaultJitterFraction = 0.2;

    /// <summary>
    /// Calculates how long to pause a host that has just rate-limited us.
    /// </summary>
    /// <param name="baseBackoff">The configured backoff. Non-positive values stay non-positive.</param>
    /// <param name="attempts">
    /// How many attempts this job has already spent. Zero or one is the first refusal and yields
    /// the base delay; each further attempt doubles it, up to <see cref="MaxDoublings"/>.
    /// </param>
    /// <param name="jitterFraction">
    /// How far either side of the nominal delay the result may land, as a proportion of it. Zero
    /// disables jitter.
    /// </param>
    /// <param name="random">The source of jitter.</param>
    /// <returns>How long to wait before touching the host again.</returns>
    /// <remarks>
    /// Two things this fixes, both of which make a host angrier rather than calmer.
    ///
    /// A flat delay treats the third refusal exactly like the first, so a host that is still
    /// unhappy gets probed again on the same schedule that already failed twice; doubling backs
    /// further off each time it tells us to.
    ///
    /// The jitter matters because a cooldown is applied per host, not per job: every job queued
    /// behind the one that was refused becomes runnable at the same instant, and they arrive
    /// together as exactly the burst that tripped the limiter. Spreading the expiry breaks up the
    /// convoy.
    /// </remarks>
    public static TimeSpan ForRateLimit(
        TimeSpan baseBackoff,
        int attempts,
        double jitterFraction,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        if (baseBackoff <= TimeSpan.Zero)
        {
            // Nothing to spread, and a jittered zero could otherwise come out negative.
            return baseBackoff;
        }

        var doublings = Math.Clamp(attempts - 1, 0, MaxDoublings);
        var scaled = baseBackoff.TotalSeconds * (1L << doublings);

        if (jitterFraction > 0)
        {
            // NextDouble() is [0,1), so this spans [-jitterFraction, +jitterFraction) of the
            // nominal delay. Bounded well below 1, so the result cannot reach zero.
            var spread = Math.Min(jitterFraction, 0.9);
            scaled *= 1 + (((random.NextDouble() * 2) - 1) * spread);
        }

        return TimeSpan.FromSeconds(scaled);
    }
}
