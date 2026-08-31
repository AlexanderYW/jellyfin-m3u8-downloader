using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.M3u8Downloader;

/// <summary>
/// Downloads HLS (m3u8) streams into MKV files via a persistent, sequential queue.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths, supplied by the host.</param>
    /// <param name="xmlSerializer">Configuration serializer, supplied by the host.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    /// <remarks>
    /// The queue service reads <see cref="BasePlugin.DataFolderPath"/> and the worker reads the
    /// live configuration through this; the host only ever constructs one plugin instance.
    /// </remarks>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "M3U8 Downloader";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6f0f4a1e-6c4e-4d2a-9f3b-1c8a5d7e2b40");

    /// <inheritdoc />
    public override string Description => "Bulk-download m3u8 streams to MKV files through a sequential queue.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Create(
                CultureInfo.InvariantCulture,
                $"{GetType().Namespace}.Configuration.configPage.html"),
        };
    }
}
