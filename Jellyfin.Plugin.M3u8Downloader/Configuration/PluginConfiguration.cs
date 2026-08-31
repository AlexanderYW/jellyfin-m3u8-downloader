using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.M3u8Downloader.Configuration;

/// <summary>
/// Plugin settings, editable from the dashboard config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the directory downloads are written to.
    /// </summary>
    /// <remarks>
    /// Deliberately empty by default. Rather than guess a location and write media somewhere
    /// surprising, the queue refuses to start jobs until an admin sets this.
    /// </remarks>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many downloads may run at the same time.
    /// </summary>
    /// <remarks>
    /// Defaults to 1, which is the behaviour the plugin has always had. Raising it trades
    /// bandwidth per download for total throughput; the queue clamps it to a sane ceiling because
    /// each concurrent job is an ffmpeg process writing to the same disk.
    /// </remarks>
    public int MaxConcurrentDownloads { get; set; } = 1;

    /// <summary>
    /// Gets or sets a cap on how long any single download may record, in minutes. Zero is no cap.
    /// </summary>
    /// <remarks>
    /// The escape hatch for live streams: a source with no duration never ends on its own, so
    /// without a cap it records until the disk fills. Applied as ffmpeg's <c>-t</c>, and only to
    /// sources whose length ffprobe could not determine -- capping a measured three-hour film
    /// would truncate it silently and still report the job as completed. See
    /// <see cref="LimitLengthOnAllDownloads"/>.
    /// </remarks>
    public int MaxDurationMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="MaxDurationMinutes"/> also applies to
    /// sources of known length, truncating them.
    /// </summary>
    /// <remarks>
    /// Off by default, so the cap only does the job it exists for. On, it is a blanket limit on
    /// how much any one job may write, for anyone who wants that guarantee more than they want
    /// long files intact.
    /// </remarks>
    public bool LimitLengthOnAllDownloads { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the queue is paused.
    /// </summary>
    /// <remarks>
    /// Stops the worker claiming anything new. Downloads already running are left to finish: they
    /// cannot be resumed later, so stopping one is a cancel, and that stays an explicit choice.
    /// </remarks>
    public bool QueuePaused { get; set; }

    /// <summary>
    /// Gets or sets how many days finished jobs are kept before being dropped. Zero keeps them
    /// until they are cleared by hand.
    /// </summary>
    /// <remarks>
    /// Off by default: pruning history is a convenience, and silently discarding a record of what
    /// was downloaded is not something to switch on for an existing install without being asked.
    /// </remarks>
    public int HistoryRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets how many times a failed job is retried before being marked failed.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Gets or sets how long to wait before retrying a failed job.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long a download may make no progress before it is killed. Zero disables
    /// the check.
    /// </summary>
    /// <remarks>
    /// ffmpeg reports an advancing position roughly once a second while it is muxing, so a window
    /// this long with no advance at all is a dead stream rather than a slow one -- the reconnect
    /// options this plugin sets by default will otherwise retry a silent host indefinitely, and the
    /// job holds its queue slot until someone cancels it by hand.
    ///
    /// On by default, unlike <see cref="MaxDurationMinutes"/>: tripping this wrongly costs a retry,
    /// whereas capping a source of known length would silently truncate it.
    /// </remarks>
    public int StallTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the User-Agent header sent to the stream host. Empty uses ffmpeg's default.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Referer header sent to the stream host. Empty sends none.
    /// </summary>
    public string Referer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether ffmpeg may reuse one HTTP connection across
    /// segments.
    /// </summary>
    /// <remarks>
    /// Off by default. Many CDNs serve consecutive segments from rotating hostnames, and reusing
    /// a connection across them produces
    /// <c>Cannot reuse HTTP connection for different host</c> followed by truncated segments and
    /// a failed download. Turning it on is slightly faster on CDNs that use a stable hostname.
    /// Only applies to HLS inputs.
    /// </remarks>
    public bool ReuseHttpConnections { get; set; }

    /// <summary>
    /// Gets or sets extra ffmpeg arguments placed before <c>-i</c>, as input options.
    /// </summary>
    /// <remarks>
    /// Options such as <c>-reconnect</c>, <c>-http_persistent</c> and <c>-seg_max_retry</c> only
    /// take effect before the input and are ignored (or rejected) after it, which is why they
    /// cannot be supplied through <see cref="ExtraFfmpegArgs"/>.
    /// Split on whitespace, honouring double quotes.
    /// </remarks>
    public string ExtraInputArgs { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets extra ffmpeg arguments, inserted before the output path as output options.
    /// </summary>
    /// <remarks>Split on whitespace, honouring double quotes.</remarks>
    public string ExtraFfmpegArgs { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to trigger a library scan after each download.
    /// </summary>
    public bool ScanLibraryAfterDownload { get; set; } = true;
}
