using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class FfmpegArgumentTests
{
    private const string Url = "https://example.com/a.m3u8";
    private const string Output = "/media/out.mkv.part";

    [Fact]
    public void BuildDownloadArguments_NoProgram_MapsEveryVideoAudioAndSubtitleStream()
    {
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration());

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1",
                "-reconnect", "1", "-reconnect_streamed", "1",
                "-reconnect_on_network_error", "1", "-reconnect_delay_max", "30",
                "-readrate", "10",
                "-err_detect", "ignore_err", "-fflags", "+discardcorrupt",
                "-i", Url,
                "-map", "0:v?", "-map", "0:a?", "-map", "0:s?",
                "-c", "copy",
                "-f", "matroska", "-y", Output,
            },
            args);
    }

    [Fact]
    public void BuildDownloadArguments_WithProgram_MapsThatProgramAndDropsDataStreams()
    {
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration(), bestProgramId: 4);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1",
                "-reconnect", "1", "-reconnect_streamed", "1",
                "-reconnect_on_network_error", "1", "-reconnect_delay_max", "30",
                "-readrate", "10",
                "-err_detect", "ignore_err", "-fflags", "+discardcorrupt",
                "-i", Url,
                "-map", "0:p:4", "-dn",
                "-c", "copy",
                "-f", "matroska", "-y", Output,
            },
            args);
    }

    [Fact]
    public void BuildDownloadArguments_WithProgram_KeepsAllAudioAndSubtitlesByNotNamingStreamTypes()
    {
        // Mapping the program is what preserves every audio track and subtitle language; naming
        // types individually here would re-introduce the duplicate-video problem on a master
        // playlist. Guard against someone "simplifying" this back to per-type mapping.
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration(), bestProgramId: 0);

        Assert.Contains("0:p:0", args);
        Assert.DoesNotContain("0:v?", args);
        Assert.DoesNotContain("0:a?", args);
    }

    [Fact]
    public void BuildDownloadArguments_NoProgram_OmitsDnBecauseTypedMapsCannotSelectDataStreams()
    {
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration());

        Assert.DoesNotContain("-dn", args);
    }

    [Fact]
    public void BuildDownloadArguments_OmitsHeaderFlagsWhenUnset()
    {
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration());

        Assert.DoesNotContain("-user_agent", args);
        Assert.DoesNotContain("-headers", args);
    }

    [Fact]
    public void BuildDownloadArguments_PutsUserAgentAndRefererBeforeTheInput()
    {
        var config = new PluginConfiguration
        {
            UserAgent = "Mozilla/5.0",
            Referer = "https://example.com/",
        };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.Equal("Mozilla/5.0", args[args.IndexOf("-user_agent") + 1]);
        Assert.Equal("Referer: https://example.com/\r\n", args[args.IndexOf("-headers") + 1]);

        // ffmpeg only honours these as input options, i.e. before -i.
        Assert.True(args.IndexOf("-user_agent") < args.IndexOf("-i"));
        Assert.True(args.IndexOf("-headers") < args.IndexOf("-i"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    public void BuildDownloadArguments_PlacesExtraArgsBeforeTheOutputPath(int? programId)
    {
        var config = new PluginConfiguration { ExtraFfmpegArgs = "-bsf:a aac_adtstoasc" };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config, programId).ToList();

        Assert.True(args.IndexOf("aac_adtstoasc") < args.IndexOf(Output));
        Assert.Equal(Output, args[^1]);
    }

    [Fact]
    public void BuildDownloadArguments_KeepsAHostileUrlAsOneArgument()
    {
        // Quotes and spaces in a URL must not be able to change the shape of the command.
        var hostile = "https://example.com/a b.m3u8?x=\"y\" -y /etc/passwd";

        var args = FfmpegArguments.BuildDownloadArguments(hostile, Output, new PluginConfiguration()).ToList();

        Assert.Equal(hostile, args[args.IndexOf("-i") + 1]);
        Assert.Single(args, a => a == hostile);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("-a -b", new[] { "-a", "-b" })]
    [InlineData("  -a   -b  ", new[] { "-a", "-b" })]
    [InlineData("-metadata \"title=Some Show\"", new[] { "-metadata", "title=Some Show" })]
    public void SplitArguments_HonoursQuotes(string? input, string[] expected)
    {
        Assert.Equal(expected, FfmpegArguments.SplitArguments(input));
    }

    [Theory]
    [InlineData("out_time_us=5000000", 5d)]
    [InlineData("out_time_ms=2500000", 2.5d)]
    [InlineData("out_time=00:00:30.5", 30.5d)]
    public void ParseProgressLine_ReadsPosition(string line, double expected)
    {
        var sample = FfmpegOutputClassifier.ParseProgressLine(line);

        Assert.Equal(expected, sample.PositionSeconds);
        Assert.Null(sample.SpeedRatio);
    }

    [Theory]
    [InlineData("speed=1.02x", 1.02d)]
    [InlineData("speed=  10.5x", 10.5d)]
    [InlineData("speed=0.5x", 0.5d)]
    public void ParseProgressLine_ReadsSpeed(string line, double expected)
    {
        var sample = FfmpegOutputClassifier.ParseProgressLine(line);

        Assert.Equal(expected, sample.SpeedRatio);
        Assert.Null(sample.PositionSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("progress=continue")]
    [InlineData("bitrate=N/A")]
    [InlineData("out_time_us=N/A")]
    [InlineData("speed=N/A")]
    [InlineData("speed=0x")]
    [InlineData("garbage")]
    [InlineData("=5")]
    public void ParseProgressLine_IgnoresEverythingElse(string? line)
    {
        Assert.False(FfmpegOutputClassifier.ParseProgressLine(line).HasValue);
    }

    [Fact]
    public void BuildDownloadArguments_AlwaysEnablesReconnect()
    {
        // Every reconnect option defaults to off in ffmpeg, so without these a single dropped
        // connection mid-segment ends the download.
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration()).ToList();

        foreach (var flag in new[] { "-reconnect", "-reconnect_streamed", "-reconnect_on_network_error" })
        {
            Assert.Equal("1", args[args.IndexOf(flag) + 1]);
            Assert.True(args.IndexOf(flag) < args.IndexOf("-i"), flag + " must be an input option");
        }
    }

    [Fact]
    public void BuildDownloadArguments_DiscardsCorruptPacketsByDefault()
    {
        // A truncated segment leaves a partial frame that the Matroska muxer rejects outright,
        // killing the download; these two make the demuxer drop it instead.
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration()).ToList();

        Assert.Equal("ignore_err", args[args.IndexOf("-err_detect") + 1]);
        Assert.Equal("+discardcorrupt", args[args.IndexOf("-fflags") + 1]);
        Assert.True(args.IndexOf("-err_detect") < args.IndexOf("-i"), "-err_detect must be an input option");
        Assert.True(args.IndexOf("-fflags") < args.IndexOf("-i"), "-fflags must be an input option");
    }

    [Fact]
    public void BuildDownloadArguments_KeepsCorruptPacketsWhenToleranceIsOff()
    {
        var config = new PluginConfiguration { TolerateCorruptSegments = false };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config);

        Assert.DoesNotContain("-err_detect", args);
        Assert.DoesNotContain("-fflags", args);
    }

    [Fact]
    public void BuildDownloadArguments_NonHls_OmitsHlsOnlyOptions()
    {
        // ffmpeg fails the input with "Option not found" when these reach a non-HLS demuxer.
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration(), isHls: false);

        Assert.DoesNotContain("-seg_max_retry", args);
        Assert.DoesNotContain("-http_persistent", args);
    }

    [Fact]
    public void BuildDownloadArguments_Hls_RetriesSegmentsAndReusesConnectionsByDefault()
    {
        // Keep-alive is the default because the alternative is a fresh TCP+TLS handshake per
        // segment -- around 1200 new connections for a two-hour stream, which is precisely what a
        // host's connection-rate limiter counts.
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration(), isHls: true).ToList();

        Assert.Equal("5", args[args.IndexOf("-seg_max_retry") + 1]);
        Assert.True(args.IndexOf("-seg_max_retry") < args.IndexOf("-i"));
        Assert.DoesNotContain("-http_persistent", args);
    }

    [Fact]
    public void BuildDownloadArguments_Hls_DisablesConnectionReuseWhenTurnedOff()
    {
        // The opt-out, for CDNs that rotate the hostname between segments.
        var config = new PluginConfiguration { ReuseHttpConnections = false };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config, isHls: true).ToList();

        Assert.Equal("0", args[args.IndexOf("-http_persistent") + 1]);
        Assert.True(args.IndexOf("-http_persistent") < args.IndexOf("-i"));
        Assert.Contains("-seg_max_retry", args);
    }

    [Fact]
    public void BuildDownloadArguments_PlacesExtraInputArgsBeforeTheInput()
    {
        var config = new PluginConfiguration { ExtraInputArgs = "-rw_timeout 15000000" };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.True(args.IndexOf("-rw_timeout") < args.IndexOf("-i"));
        Assert.Equal("15000000", args[args.IndexOf("-rw_timeout") + 1]);
    }

    [Fact]
    public void BuildDownloadArguments_KeepsInputAndOutputExtraArgsOnTheCorrectSideOfTheInput()
    {
        var config = new PluginConfiguration
        {
            ExtraInputArgs = "-INPUTMARK 1",
            ExtraFfmpegArgs = "-OUTPUTMARK 1",
        };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.True(args.IndexOf("-INPUTMARK") < args.IndexOf("-i"));
        Assert.True(args.IndexOf("-OUTPUTMARK") > args.IndexOf("-i"));
    }

    // ---------------------------------------------------------------- rendition fan-out

    [Fact]
    public void BuildDownloadArguments_FailedProbeOnHls_MapsNothingSoOnlyOneRenditionIsFetched()
    {
        // The whole point of this case. On a master playlist every bitrate rendition is its own
        // video stream, so "-map 0:v?" selects all of them and the HLS demuxer fetches four to six
        // variant playlists simultaneously from one host -- which reads as a scraper and gets the
        // server's IP blocked. Mapping nothing leaves ffmpeg's default selection to take a single
        // video, and only that variant is ever requested.
        var args = FfmpegArguments.BuildDownloadArguments(
            Url,
            Output,
            new PluginConfiguration(),
            bestProgramId: null,
            isHls: true,
            probeSucceeded: false);

        Assert.DoesNotContain("-map", args);
    }

    [Fact]
    public void BuildDownloadArguments_SuccessfulProbeWithNoProgram_StillMapsEveryStreamType()
    {
        // A probe that succeeded and found fewer than two programs genuinely has no renditions to
        // fan out across, so the per-type mapping is safe here and keeps every audio track.
        var args = FfmpegArguments.BuildDownloadArguments(
            Url,
            Output,
            new PluginConfiguration(),
            bestProgramId: null,
            isHls: true,
            probeSucceeded: true).ToList();

        Assert.Contains("0:v?", args);
        Assert.Contains("0:a?", args);
        Assert.Contains("0:s?", args);
    }

    [Fact]
    public void BuildDownloadArguments_FailedProbeOnNonHls_StillMapsEveryStreamType()
    {
        // A non-HLS input has no renditions, so a failed probe there costs nothing and the extra
        // audio tracks are worth keeping.
        var args = FfmpegArguments.BuildDownloadArguments(
            Url,
            Output,
            new PluginConfiguration(),
            bestProgramId: null,
            isHls: false,
            probeSucceeded: false).ToList();

        Assert.Contains("0:v?", args);
    }

    [Fact]
    public void BuildDownloadArguments_KnownProgram_IgnoresTheProbeFlag()
    {
        // A program id can only have come from a successful probe, but the program map must win
        // regardless -- it is strictly better than either fallback.
        var args = FfmpegArguments.BuildDownloadArguments(
            Url,
            Output,
            new PluginConfiguration(),
            bestProgramId: 4,
            isHls: true,
            probeSucceeded: false).ToList();

        Assert.Contains("0:p:4", args);
        Assert.DoesNotContain("0:v?", args);
    }

    // ---------------------------------------------------------------- speed limit

    [Fact]
    public void BuildDownloadArguments_PacesTheDownloadByDefault()
    {
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration()).ToList();

        Assert.Equal("10", args[args.IndexOf("-readrate") + 1]);
        Assert.True(args.IndexOf("-readrate") < args.IndexOf("-i"), "-readrate must be an input option");
    }

    [Fact]
    public void BuildDownloadArguments_OmitsTheSpeedLimitWhenZero()
    {
        // 0 is the escape hatch for an ffmpeg older than 5.1, which rejects the option outright.
        var config = new PluginConfiguration { MaxSpeedMultiplier = 0 };

        Assert.DoesNotContain("-readrate", FfmpegArguments.BuildDownloadArguments(Url, Output, config));
    }

    [Fact]
    public void BuildDownloadArguments_FormatsAFractionalSpeedLimitInvariantly()
    {
        // A locale that writes "2,5" would have ffmpeg reject the whole input.
        var config = new PluginConfiguration { MaxSpeedMultiplier = 2.5 };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.Equal("2.5", args[args.IndexOf("-readrate") + 1]);
    }

    // ---------------------------------------------------------------- request headers

    [Fact]
    public void BuildDownloadArguments_StripsNewlinesFromTheReferer()
    {
        // The -headers value is a CRLF-separated block, so a break inside one entry does not stay
        // inside it. A Referer pasted from a browser's network panel routinely carries one.
        var config = new PluginConfiguration { Referer = "https://example.com/watch\r\n" };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.Equal("Referer: https://example.com/watch\r\n", args[args.IndexOf("-headers") + 1]);
    }

    [Fact]
    public void BuildDownloadArguments_RefererCannotSmuggleAFurtherHeader()
    {
        var config = new PluginConfiguration { Referer = "https://example.com\r\nX-Injected: 1" };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        var value = args[args.IndexOf("-headers") + 1];
        Assert.Equal("Referer: https://example.comX-Injected: 1\r\n", value);
        Assert.Single(args, a => a == "-headers");
    }

    [Fact]
    public void BuildDownloadArguments_StripsNewlinesFromTheUserAgent()
    {
        var config = new PluginConfiguration { UserAgent = "Mozilla/5.0\n" };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.Equal("Mozilla/5.0", args[args.IndexOf("-user_agent") + 1]);
    }

    [Fact]
    public void BuildDownloadArguments_ARefererOfOnlyWhitespaceIsNotSent()
    {
        var config = new PluginConfiguration { Referer = "\r\n  " };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.DoesNotContain("-headers", args);
    }

    // ---------------------------------------------------------------- probe arguments

    [Fact]
    public void BuildProbeArguments_AsksForTheFormatAndProgramsAsJson()
    {
        var args = FfmpegArguments.BuildProbeArguments(Url, new PluginConfiguration());

        Assert.Equal(
            new[]
            {
                "-v", "quiet", "-print_format", "json", "-show_format", "-show_programs",
                "-i", Url,
            },
            args);
    }

    [Fact]
    public void BuildProbeArguments_SendsTheSameHeadersAsTheDownload()
    {
        // A host that gates on Referer would reject the probe while accepting the download. That
        // costs the duration and, worse, the program selection -- which silently drops the
        // download into mapping every bitrate rendition of a master playlist.
        var config = new PluginConfiguration
        {
            UserAgent = "TestAgent/1.0",
            Referer = "https://example.com/",
        };

        var args = FfmpegArguments.BuildProbeArguments(Url, config);

        Assert.Equal(
            new[]
            {
                "-v", "quiet", "-print_format", "json", "-show_format", "-show_programs",
                "-user_agent", "TestAgent/1.0",
                "-headers", "Referer: https://example.com/\r\n",
                "-i", Url,
            },
            args);
    }

    [Fact]
    public void BuildProbeArguments_DoesNotForwardFfmpegOnlyInputArgs()
    {
        // ffprobe would fail the input outright on an option it does not define, which is the very
        // outcome sharing the headers exists to avoid.
        var config = new PluginConfiguration { ExtraInputArgs = "-rw_timeout 5000000" };

        Assert.DoesNotContain("-rw_timeout", FfmpegArguments.BuildProbeArguments(Url, config));
    }

    // ---------------------------------------------------------------- duration cap

    [Fact]
    public void BuildDownloadArguments_NoDurationCapByDefault()
    {
        Assert.DoesNotContain("-t", FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration()));
    }

    [Fact]
    public void BuildDownloadArguments_DurationCapIsConvertedToSeconds()
    {
        var config = new PluginConfiguration { MaxDurationMinutes = 90 };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config);

        var index = args.ToList().IndexOf("-t");
        Assert.True(index >= 0);
        Assert.Equal("5400", args[index + 1]);
    }

    [Fact]
    public void BuildDownloadArguments_KnownDuration_DoesNotApplyTheLengthCap()
    {
        // The cap exists for streams that never end. Applying it to a source ffprobe measured
        // would truncate it silently and still report the job as completed.
        var config = new PluginConfiguration { MaxDurationMinutes = 60 };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config, hasKnownDuration: true);

        Assert.DoesNotContain("-t", args);
    }

    [Fact]
    public void BuildDownloadArguments_LimitLengthOnAllDownloads_CapsEvenAKnownDuration()
    {
        var config = new PluginConfiguration { MaxDurationMinutes = 60, LimitLengthOnAllDownloads = true };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config, hasKnownDuration: true).ToList();

        Assert.Equal("3600", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void BuildDownloadArguments_DurationCapIsAnOutputOptionTheUserCanOverride()
    {
        var config = new PluginConfiguration
        {
            MaxDurationMinutes = 5,
            ExtraFfmpegArgs = "-t 60",
        };

        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, config).ToList();

        // After -i, so it applies to the output; before the user's own args, so theirs wins.
        Assert.InRange(args.IndexOf("-t"), args.IndexOf("-i"), args.Count);
        Assert.Equal("300", args[args.IndexOf("-t") + 1]);
        Assert.Equal("60", args[args.LastIndexOf("-t") + 1]);
    }
}
