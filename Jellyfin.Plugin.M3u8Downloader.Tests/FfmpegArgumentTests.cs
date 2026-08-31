using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class FfmpegArgumentTests
{
    private const string Url = "https://example.com/a.m3u8";
    private const string Output = "/media/out.mkv.part";

    /// <summary>An absolute root, so the resolver's containment check has something to work with.</summary>
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "m3u8-publish-tests"));

    [Fact]
    public void BuildDownloadArguments_NoProgram_MapsEveryVideoAudioAndSubtitleStream()
    {
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration());

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1",
                "-reconnect", "1", "-reconnect_streamed", "1",
                "-reconnect_on_network_error", "1", "-reconnect_delay_max", "30",
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
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration(), bestProgramId: 4);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1",
                "-reconnect", "1", "-reconnect_streamed", "1",
                "-reconnect_on_network_error", "1", "-reconnect_delay_max", "30",
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
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration(), bestProgramId: 0);

        Assert.Contains("0:p:0", args);
        Assert.DoesNotContain("0:v?", args);
        Assert.DoesNotContain("0:a?", args);
    }

    [Fact]
    public void BuildDownloadArguments_NoProgram_OmitsDnBecauseTypedMapsCannotSelectDataStreams()
    {
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration());

        Assert.DoesNotContain("-dn", args);
    }

    [Fact]
    public void BuildDownloadArguments_OmitsHeaderFlagsWhenUnset()
    {
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration());

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

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config).ToList();

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

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config, programId).ToList();

        Assert.True(args.IndexOf("aac_adtstoasc") < args.IndexOf(Output));
        Assert.Equal(Output, args[^1]);
    }

    [Fact]
    public void BuildDownloadArguments_KeepsAHostileUrlAsOneArgument()
    {
        // Quotes and spaces in a URL must not be able to change the shape of the command.
        var hostile = "https://example.com/a b.m3u8?x=\"y\" -y /etc/passwd";

        var args = FfmpegDownloader.BuildDownloadArguments(hostile, Output, new PluginConfiguration()).ToList();

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
        Assert.Equal(expected, FfmpegDownloader.SplitArguments(input));
    }

    [Theory]
    [InlineData("out_time_us=5000000", 5d)]
    [InlineData("out_time_ms=2500000", 2.5d)]
    [InlineData("out_time=00:00:30.5", 30.5d)]
    public void ParseProgressLine_ReadsPosition(string line, double expected)
    {
        var sample = FfmpegDownloader.ParseProgressLine(line);

        Assert.Equal(expected, sample.PositionSeconds);
        Assert.Null(sample.SpeedRatio);
    }

    [Theory]
    [InlineData("speed=1.02x", 1.02d)]
    [InlineData("speed=  10.5x", 10.5d)]
    [InlineData("speed=0.5x", 0.5d)]
    public void ParseProgressLine_ReadsSpeed(string line, double expected)
    {
        var sample = FfmpegDownloader.ParseProgressLine(line);

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
        Assert.False(FfmpegDownloader.ParseProgressLine(line).HasValue);
    }

    [Fact]
    public void BuildDownloadArguments_AlwaysEnablesReconnect()
    {
        // Every reconnect option defaults to off in ffmpeg, so without these a single dropped
        // connection mid-segment ends the download.
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration()).ToList();

        foreach (var flag in new[] { "-reconnect", "-reconnect_streamed", "-reconnect_on_network_error" })
        {
            Assert.Equal("1", args[args.IndexOf(flag) + 1]);
            Assert.True(args.IndexOf(flag) < args.IndexOf("-i"), flag + " must be an input option");
        }
    }

    [Fact]
    public void BuildDownloadArguments_NonHls_OmitsHlsOnlyOptions()
    {
        // ffmpeg fails the input with "Option not found" when these reach a non-HLS demuxer.
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration(), isHls: false);

        Assert.DoesNotContain("-seg_max_retry", args);
        Assert.DoesNotContain("-http_persistent", args);
    }

    [Fact]
    public void BuildDownloadArguments_Hls_RetriesSegmentsAndDisablesConnectionReuse()
    {
        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration(), isHls: true).ToList();

        Assert.Equal("5", args[args.IndexOf("-seg_max_retry") + 1]);
        Assert.Equal("0", args[args.IndexOf("-http_persistent") + 1]);
        Assert.True(args.IndexOf("-seg_max_retry") < args.IndexOf("-i"));
        Assert.True(args.IndexOf("-http_persistent") < args.IndexOf("-i"));
    }

    [Fact]
    public void BuildDownloadArguments_Hls_KeepsConnectionReuseWhenEnabled()
    {
        var config = new PluginConfiguration { ReuseHttpConnections = true };

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config, isHls: true);

        Assert.DoesNotContain("-http_persistent", args);
        Assert.Contains("-seg_max_retry", args);
    }

    [Fact]
    public void BuildDownloadArguments_PlacesExtraInputArgsBeforeTheInput()
    {
        var config = new PluginConfiguration { ExtraInputArgs = "-rw_timeout 15000000" };

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config).ToList();

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

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config).ToList();

        Assert.True(args.IndexOf("-INPUTMARK") < args.IndexOf("-i"));
        Assert.True(args.IndexOf("-OUTPUTMARK") > args.IndexOf("-i"));
    }

    // ---------------------------------------------------------------- probe arguments

    [Fact]
    public void BuildProbeArguments_AsksForTheFormatAndProgramsAsJson()
    {
        var args = FfmpegDownloader.BuildProbeArguments(Url, new PluginConfiguration());

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

        var args = FfmpegDownloader.BuildProbeArguments(Url, config);

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

        Assert.DoesNotContain("-rw_timeout", FfmpegDownloader.BuildProbeArguments(Url, config));
    }

    // ---------------------------------------------------------------- publishing

    [Fact]
    public void Publish_FreeName_MovesStraightOntoIt()
    {
        var moves = new List<(string From, string To)>();

        var landed = FfmpegDownloader.Publish(
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

        var landed = FfmpegDownloader.Publish(
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

        Assert.Throws<IOException>(() => FfmpegDownloader.Publish(
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

        Assert.Throws<IOException>(() => FfmpegDownloader.Publish(
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

        var landed = FfmpegDownloader.Publish(
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

    // ---------------------------------------------------------------- duration cap

    [Fact]
    public void BuildDownloadArguments_NoDurationCapByDefault()
    {
        Assert.DoesNotContain("-t", FfmpegDownloader.BuildDownloadArguments(Url, Output, new PluginConfiguration()));
    }

    [Fact]
    public void BuildDownloadArguments_DurationCapIsConvertedToSeconds()
    {
        var config = new PluginConfiguration { MaxDurationMinutes = 90 };

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config);

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

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config, hasKnownDuration: true);

        Assert.DoesNotContain("-t", args);
    }

    [Fact]
    public void BuildDownloadArguments_LimitLengthOnAllDownloads_CapsEvenAKnownDuration()
    {
        var config = new PluginConfiguration { MaxDurationMinutes = 60, LimitLengthOnAllDownloads = true };

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config, hasKnownDuration: true).ToList();

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

        var args = FfmpegDownloader.BuildDownloadArguments(Url, Output, config).ToList();

        // After -i, so it applies to the output; before the user's own args, so theirs wins.
        Assert.InRange(args.IndexOf("-t"), args.IndexOf("-i"), args.Count);
        Assert.Equal("300", args[args.IndexOf("-t") + 1]);
        Assert.Equal("60", args[args.LastIndexOf("-t") + 1]);
    }
}
