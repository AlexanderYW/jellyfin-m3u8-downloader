using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class FfmpegOutputClassifierTests
{
    private const string Url = "https://example.com/a.m3u8";
    private const string Output = "/media/out.mkv.part";

    // ---------------------------------------------------------------- rate-limit detection

    [Theory]
    [InlineData("[https @ 0x55] HTTP error 429 Too Many Requests")]
    [InlineData("[https @ 0x55] HTTP error 403 Forbidden")]
    [InlineData("Server returned 403 Forbidden (access denied)")]
    [InlineData("Server returned 429 Too Many Requests")]
    public void IsRateLimited_RecognisesAHostRefusingUs(string line)
    {
        Assert.True(FfmpegOutputClassifier.IsRateLimited(new[] { "Opening 'https://example.com/1.ts'", line }));
    }

    [Theory]
    [InlineData("Error muxing a packet")]
    [InlineData("Stream ends prematurely at 12345, should be 23456")]
    [InlineData("[https @ 0x55] HTTP error 404 Not Found")]
    [InlineData("Server returned 500 Internal Server Error")]
    // A three-digit run on its own is not a status code: ffmpeg puts segment numbers and whole
    // URLs in its errors, and treating either as a refusal parks every job on the host.
    [InlineData("[hls @ 0x55] Failed to open segment 1403 of playlist 0")]
    [InlineData("Opening 'https://cdn.example.com/seg_4290.ts' for reading")]
    [InlineData("[hls @ 0x55] keepalive request failed for 'https://x/403/a.ts'")]
    // "Forbidden" counts only next to a 403.
    [InlineData("Forbidden characters in the output file name")]
    [InlineData("")]
    public void IsRateLimited_LeavesOrdinaryFailuresToTheNormalRetry(string line)
    {
        Assert.False(FfmpegOutputClassifier.IsRateLimited(new[] { line }));
    }

    [Fact]
    public void IsRateLimited_EmptyOutput_IsNotARateLimit()
    {
        Assert.False(FfmpegOutputClassifier.IsRateLimited(Array.Empty<string>()));
    }

    [Fact]
    public void BuildDownloadArguments_CopiesAudioByDefault()
    {
        var args = FfmpegArguments.BuildDownloadArguments(Url, Output, new PluginConfiguration());

        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void BuildDownloadArguments_ReencodeAudio_OverridesOnlyTheAudioCodec()
    {
        var args = FfmpegArguments.BuildDownloadArguments(
            Url, Output, new PluginConfiguration(), reencodeAudio: true).ToList();

        // "-c copy" must stay so video and subtitles are still copied; "-c:a aac" only wins
        // because it comes after it.
        Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.True(args.IndexOf("-c") < args.IndexOf("-c:a"), "-c:a must override -c copy");
    }

    [Fact]
    public void IsAudioBitstreamFailure_RecognisesTheMuxDyingInsideTheFilter()
    {
        var stderr = new[]
        {
            "[https @ 0x55] Stream ends prematurely at 540411, should be 574904",
            "[aac_adtstoasc @ 0x55] Error parsing ADTS frame header!",
            "[matroska @ 0x55] Error applying bitstream filters to an output packet for stream #0: Invalid data found when processing input",
            "[out#0/matroska @ 0x55] Error muxing a packet",
        };

        Assert.True(FfmpegOutputClassifier.IsAudioBitstreamFailure(stderr));
    }

    [Theory]
    [InlineData("[aac_adtstoasc @ 0x55] Error parsing ADTS frame header!")]
    [InlineData("[matroska @ 0x55] Error applying bitstream filters to an output packet for stream #0")]
    [InlineData("[out#0/matroska @ 0x55] Error muxing a packet")]
    [InlineData("Invalid data found when processing input")]
    public void IsAudioBitstreamFailure_NeedsBothHalvesOfTheSignature(string line)
    {
        // Either half alone is too weak to justify spending a second full download on.
        Assert.False(FfmpegOutputClassifier.IsAudioBitstreamFailure(new[] { line }));
    }

    [Fact]
    public void IsAudioBitstreamFailure_EmptyOutput_IsNotABitstreamFailure()
    {
        Assert.False(FfmpegOutputClassifier.IsAudioBitstreamFailure(Array.Empty<string>()));
    }
}
