using System;
using System.Collections.Generic;
using Jellyfin.Plugin.M3u8Downloader.Configuration;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Points a child ffmpeg or ffprobe at the configured HTTP proxy.
/// </summary>
/// <remarks>
/// Separate from <see cref="FfmpegDownloader"/> because deciding what a proxy setting means -- and
/// whether it is one ffmpeg can use at all -- involves no process, and because the redaction has to
/// be right every time a proxy is logged.
/// </remarks>
public static class ProxySettings
{
    /// <summary>
    /// Determines whether a configured proxy value is one ffmpeg can actually use.
    /// </summary>
    /// <param name="value">The configured proxy URL.</param>
    /// <returns><c>true</c> when the value is an absolute <c>http</c> URL.</returns>
    /// <remarks>
    /// ffmpeg only speaks to HTTP proxies, and reaches them over plain HTTP even for an
    /// <c>https</c> stream, which it tunnels with <c>CONNECT</c>. A <c>socks5://</c> or
    /// <c>https://</c> value would be accepted silently by the environment and then ignored -- or
    /// worse, misparsed -- so it is rejected here where it can be reported instead.
    /// </remarks>
    public static bool IsUsableProxyUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies the configured proxy to a child process environment.
    /// </summary>
    /// <param name="environment">The child process environment to modify.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <returns>
    /// A redacted description of the proxy that was applied, or <c>null</c> when none was
    /// configured or the configured value was unusable.
    /// </returns>
    /// <remarks>
    /// The proxy travels as an environment variable rather than ffmpeg's <c>-http_proxy</c> input
    /// option deliberately. The option applies to the input ffmpeg opens directly, which leaves
    /// every nested HTTP request an HLS playlist makes -- segments, AES key URIs, variant playlists
    /// -- going out unproxied; the environment variable covers all of them. Both the lowercase and
    /// uppercase spellings are set because different builds and helper tools read different ones.
    ///
    /// Setting this on the child only is the whole point: the rest of the server keeps its own
    /// networking.
    /// </remarks>
    public static string? ApplyProxyEnvironment(IDictionary<string, string?> environment, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(config);

        if (!IsUsableProxyUrl(config.ProxyUrl))
        {
            return null;
        }

        var proxy = config.ProxyUrl.Trim();
        environment["http_proxy"] = proxy;
        environment["https_proxy"] = proxy;
        environment["HTTP_PROXY"] = proxy;
        environment["HTTPS_PROXY"] = proxy;

        if (!string.IsNullOrWhiteSpace(config.ProxyBypassList))
        {
            var bypass = config.ProxyBypassList.Trim();
            environment["no_proxy"] = bypass;
            environment["NO_PROXY"] = bypass;
        }

        return RedactProxy(proxy);
    }

    /// <summary>
    /// Strips any credentials from a proxy URL so it can be logged.
    /// </summary>
    /// <param name="proxy">The proxy URL.</param>
    /// <returns>The URL with any <c>user:pass@</c> replaced by <c>***@</c>.</returns>
    private static string RedactProxy(string proxy)
    {
        var schemeEnd = proxy.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return proxy;
        }

        // Only the userinfo section can carry a password, and it ends at the first '@' of the
        // authority -- which is also the last one, since '@' is not legal in a host.
        var authority = schemeEnd + 3;
        var at = proxy.IndexOf('@', authority);
        return at < 0
            ? proxy
            : string.Concat(proxy.AsSpan(0, authority), "***", proxy.AsSpan(at));
    }
}
