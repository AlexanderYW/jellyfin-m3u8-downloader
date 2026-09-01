using System;

namespace Jellyfin.Plugin.M3u8Downloader.Model;

/// <summary>
/// Thrown when ffmpeg failed because the host is rate-limiting or blocking us.
/// </summary>
/// <remarks>
/// A distinct type because the correct response differs from any other download failure. The
/// ordinary path requeues the job after a few seconds, which -- since a remux has no resume point
/// and restarts from the first segment -- sends the host another full run of requests while it is
/// still angry, turning a soft throttle into a hard block. A rate limit instead earns a long
/// cooldown applied to every queued job on that host, because it is the host that is refusing us,
/// not this one URL.
///
/// Derives from <see cref="InvalidOperationException"/> so that any handler already catching the
/// downloader's ordinary failure still catches this one.
/// </remarks>
public class RateLimitedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitedException"/> class.
    /// </summary>
    public RateLimitedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitedException"/> class.
    /// </summary>
    /// <param name="message">The failure description, including ffmpeg's own output.</param>
    public RateLimitedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitedException"/> class.
    /// </summary>
    /// <param name="message">The failure description, including ffmpeg's own output.</param>
    /// <param name="innerException">The underlying cause.</param>
    public RateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
