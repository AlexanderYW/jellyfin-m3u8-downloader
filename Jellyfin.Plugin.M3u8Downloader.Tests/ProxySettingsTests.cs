using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class ProxySettingsTests
{
    // ---------------------------------------------------------------- proxy

    [Fact]
    public void ApplyProxyEnvironment_SetsEveryProxyVariableSpelling()
    {
        var config = new PluginConfiguration { ProxyUrl = "http://10.0.0.5:8080" };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        var applied = ProxySettings.ApplyProxyEnvironment(env, config);

        Assert.Equal("http://10.0.0.5:8080", applied);
        Assert.Equal("http://10.0.0.5:8080", env["http_proxy"]);
        Assert.Equal("http://10.0.0.5:8080", env["https_proxy"]);
        Assert.Equal("http://10.0.0.5:8080", env["HTTP_PROXY"]);
        Assert.Equal("http://10.0.0.5:8080", env["HTTPS_PROXY"]);

        // https_proxy still points at an http:// endpoint: ffmpeg reaches the proxy in the clear
        // and tunnels TLS through it with CONNECT.
        Assert.DoesNotContain("no_proxy", env.Keys);
    }

    [Fact]
    public void ApplyProxyEnvironment_LeavesTheEnvironmentAloneWhenNoProxyIsConfigured()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        Assert.Null(ProxySettings.ApplyProxyEnvironment(env, new PluginConfiguration()));
        Assert.Empty(env);
    }

    [Theory]
    [InlineData("socks5://10.0.0.5:1080")]
    [InlineData("https://10.0.0.5:8080")]
    [InlineData("10.0.0.5:8080")]
    [InlineData("not a url")]
    public void ApplyProxyEnvironment_RejectsAnythingFfmpegCannotUse(string proxy)
    {
        // Applying one of these would leave the download silently unproxied, which is the one
        // outcome a proxy user must not get by accident.
        var config = new PluginConfiguration { ProxyUrl = proxy };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        Assert.Null(ProxySettings.ApplyProxyEnvironment(env, config));
        Assert.Empty(env);
    }

    [Fact]
    public void ApplyProxyEnvironment_SetsTheBypassListWhenGiven()
    {
        var config = new PluginConfiguration
        {
            ProxyUrl = "http://10.0.0.5:8080",
            ProxyBypassList = "localhost,127.0.0.1,.lan",
        };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        ProxySettings.ApplyProxyEnvironment(env, config);

        Assert.Equal("localhost,127.0.0.1,.lan", env["no_proxy"]);
        Assert.Equal("localhost,127.0.0.1,.lan", env["NO_PROXY"]);
    }

    [Fact]
    public void ApplyProxyEnvironment_IgnoresTheBypassListWithoutAProxy()
    {
        var config = new PluginConfiguration { ProxyBypassList = "localhost" };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        ProxySettings.ApplyProxyEnvironment(env, config);

        Assert.Empty(env);
    }

    [Fact]
    public void ApplyProxyEnvironment_PassesCredentialsThroughButRedactsThemFromTheReport()
    {
        var config = new PluginConfiguration { ProxyUrl = "  http://bob:hunter2@10.0.0.5:8080  " };
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        var applied = ProxySettings.ApplyProxyEnvironment(env, config);

        // ffmpeg needs the real credentials; the log line must not have them.
        Assert.Equal("http://bob:hunter2@10.0.0.5:8080", env["http_proxy"]);
        Assert.Equal("http://***@10.0.0.5:8080", applied);
    }
}
