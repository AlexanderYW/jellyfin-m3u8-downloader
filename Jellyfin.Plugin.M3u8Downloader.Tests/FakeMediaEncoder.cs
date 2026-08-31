using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

/// <summary>
/// A media encoder that only answers where ffmpeg lives.
/// </summary>
/// <remarks>
/// That is the only thing the worker asks of it: an empty <see cref="EncoderPath"/> is how the
/// server says it has not finished initialising, and the worker waits rather than claiming work.
/// Every other member throws, so a test that starts to depend on more fails loudly instead of
/// quietly succeeding against a default.
/// </remarks>
internal sealed class FakeMediaEncoder : IMediaEncoder
{
    public string EncoderPath => "/usr/bin/ffmpeg";

    public string ProbePath => "/usr/bin/ffprobe";

    public Version EncoderVersion => throw Unsupported();

    public bool IsPkeyPauseSupported => throw Unsupported();

    public bool IsVaapiDeviceAmd => throw Unsupported();

    public bool IsVaapiDeviceInteliHD => throw Unsupported();

    public bool IsVaapiDeviceInteli965 => throw Unsupported();

    public bool IsVaapiDeviceSupportVulkanDrmModifier => throw Unsupported();

    public bool IsVaapiDeviceSupportVulkanDrmInterop => throw Unsupported();

    public bool IsVideoToolboxAv1DecodeAvailable => throw Unsupported();

    public bool SupportsEncoder(string encoder) => throw Unsupported();

    public bool SupportsDecoder(string decoder) => throw Unsupported();

    public bool SupportsHwaccel(string hwaccel) => throw Unsupported();

    public bool SupportsFilter(string filter) => throw Unsupported();

    public bool SupportsFilterWithOption(FilterOptionType option) => throw Unsupported();

    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => throw Unsupported();

    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<string> ExtractVideoImage(
        string inputFile,
        string container,
        MediaSourceInfo mediaSource,
        MediaStream videoStream,
        Video3DFormat? threedFormat,
        TimeSpan? offset,
        CancellationToken cancellationToken) => throw Unsupported();

    public Task<string> ExtractVideoImage(
        string inputFile,
        string container,
        MediaSourceInfo mediaSource,
        MediaStream imageStream,
        int? imageStreamIndex,
        ImageFormat? targetFormat,
        CancellationToken cancellationToken) => throw Unsupported();

    public Task<string> ExtractVideoImagesOnIntervalAccelerated(
        string inputFile,
        string container,
        MediaSourceInfo mediaSource,
        MediaStream imageStream,
        int maxWidth,
        TimeSpan interval,
        bool allowHwAccel,
        bool enableHwEncoding,
        int? threads,
        int? qualityScale,
        ProcessPriorityClass? priority,
        bool enableKeyFrameOnlyExtraction,
        EncodingHelper encodingHelper,
        CancellationToken cancellationToken) => throw Unsupported();

    public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken) =>
        throw Unsupported();

    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => throw Unsupported();

    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) =>
        throw Unsupported();

    public string GetExternalSubtitleInputArgument(string inputFile) => throw Unsupported();

    public string GetTimeParameter(long ticks) => throw Unsupported();

    public Task ConvertImage(string inputPath, string outputPath) => throw Unsupported();

    public string EscapeSubtitleFilterPath(string path) => throw Unsupported();

    public bool SetFFmpegPath() => throw Unsupported();

    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => throw Unsupported();

    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => throw Unsupported();

    public string GetInputPathArgument(EncodingJobInfo state) => throw Unsupported();

    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => throw Unsupported();

    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath) => throw Unsupported();

    public bool CanEncodeToAudioCodec(string codec) => throw Unsupported();

    public bool CanEncodeToSubtitleCodec(string codec) => throw Unsupported();

    public bool CanExtractSubtitles(string codec) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("The M3U8 Downloader tests only need the encoder path.");
}
