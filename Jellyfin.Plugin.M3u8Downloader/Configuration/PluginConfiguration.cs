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
    /// Gets or sets how many downloads may run against a single hostname at the same time.
    /// </summary>
    /// <remarks>
    /// Defaults to 1, and is applied on top of <see cref="MaxConcurrentDownloads"/> rather than
    /// instead of it: eight concurrent downloads spread across eight different sites is ordinary
    /// use, while eight aimed at one site is what gets a server's IP blocked. With this at 1, the
    /// queue simply skips past a job whose host is already busy and claims the next one that is
    /// free, so raising the global limit stays useful without concentrating the load.
    /// </remarks>
    public int MaxConcurrentPerHost { get; set; } = 1;

    /// <summary>
    /// Gets or sets how long to wait before starting another download from the same host.
    /// </summary>
    /// <remarks>
    /// A short gap so a queue of episodes from one site does not arrive as one unbroken stream of
    /// requests. Jobs on other hosts are unaffected and start immediately.
    /// </remarks>
    public int DelayBetweenDownloadsSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets how long every job on a host is held back after that host rate-limits us.
    /// </summary>
    /// <remarks>
    /// The ordinary retry delay is the wrong tool for a <c>429</c> or <c>403</c>. A remux has no
    /// resume point, so a retry re-requests the whole stream from the first segment -- while the
    /// host is still refusing us, which is how a soft throttle becomes a lasting block. The pause
    /// covers the host rather than the single job, because it is the host that is refusing us.
    /// </remarks>
    public int RateLimitBackoffMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets a cap on download speed as a multiple of realtime playback. Zero is uncapped.
    /// </summary>
    /// <remarks>
    /// Passed as ffmpeg's <c>-readrate</c>. Uncapped, ffmpeg fetches segments as fast as the
    /// connection allows: a two-hour stream lands in a couple of minutes as one dense burst of
    /// several hundred requests, which is the traffic shape rate limiters exist to catch. At the
    /// default of 10 the same stream takes about twelve minutes as a steady trickle. Lower it to
    /// 2 or 3 for a host that has already blocked you.
    ///
    /// Requires ffmpeg 5.1 or newer, which every Jellyfin 10.9+ bundle satisfies. A server pointed
    /// at an older ffmpeg will see downloads fail with <c>Option not found</c>; set this to 0
    /// there.
    /// </remarks>
    public double MaxSpeedMultiplier { get; set; } = 10;

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
    /// Gets or sets the HTTP proxy used for downloads, as <c>http://host:port</c>. Empty uses no
    /// proxy.
    /// </summary>
    /// <remarks>
    /// Applied to the ffmpeg and ffprobe child processes this plugin starts, and to nothing else --
    /// the rest of the server keeps talking to the network directly.
    ///
    /// It is passed as the <c>http_proxy</c> environment variable rather than ffmpeg's
    /// <c>-http_proxy</c> option on purpose. The option is an input option that does not reach the
    /// nested HTTP opens an HLS playlist triggers -- segments, AES key URIs, variant playlists --
    /// whereas the environment variable applies to every request the process makes.
    ///
    /// ffmpeg speaks to HTTP proxies only, so the value must use the <c>http</c> scheme even when
    /// the stream itself is <c>https</c> (it is tunnelled with <c>CONNECT</c>). SOCKS proxies are
    /// not supported. Credentials may be embedded as <c>http://user:pass@host:port</c>, in which
    /// case they are stored in plain text in this plugin's configuration file like every other
    /// setting here.
    /// </remarks>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the comma-separated hosts that bypass <see cref="ProxyUrl"/>. Empty proxies
    /// everything.
    /// </summary>
    /// <remarks>
    /// Passed through as ffmpeg's <c>no_proxy</c>, for example
    /// <c>localhost,127.0.0.1,.lan</c>. Ignored when no proxy is configured.
    /// </remarks>
    public string ProxyBypassList { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether ffmpeg may reuse one HTTP connection across
    /// segments.
    /// </summary>
    /// <remarks>
    /// On by default. Without it every segment costs a fresh TCP and TLS handshake -- roughly 1200
    /// new connections for a two-hour stream -- and connection rate is one of the first things a
    /// host's rate limiter counts, so the old default of off was a good way to look like a
    /// scraper.
    ///
    /// Turn it off for a CDN that serves consecutive segments from rotating hostnames: reusing a
    /// connection across those produces
    /// <c>Cannot reuse HTTP connection for different host</c> followed by truncated segments and a
    /// failed download. Only applies to HLS inputs.
    ///
    /// Note that flipping this default only affects new installations. An existing install has
    /// <c>false</c> written into its saved configuration, and nothing can tell that apart from a
    /// deliberate choice, so it has to be switched on by hand there.
    /// </remarks>
    public bool ReuseHttpConnections { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether damaged packets are dropped instead of failing the
    /// download.
    /// </summary>
    /// <remarks>
    /// On by default. Some hosts serve short segments -- ffmpeg reports
    /// <c>Stream ends prematurely at N, should be M</c> -- and the partial frame at the end of one
    /// reaches the Matroska muxer as an invalid packet, which aborts the whole download with
    /// <c>Error parsing ADTS frame header</c> or <c>Error muxing a packet</c>. Retrying does not
    /// help when the host truncates the same segment every time.
    ///
    /// This adds ffmpeg's <c>-fflags +discardcorrupt</c> and <c>-err_detect ignore_err</c>, so the
    /// damaged packets are discarded and the rest of the stream is written. The cost is a brief
    /// glitch where each truncated segment ends, in exchange for a file instead of a failure; a
    /// stream with no damaged packets is unaffected. Turn it off to have such a download fail
    /// loudly rather than complete with gaps.
    /// </remarks>
    public bool TolerateCorruptSegments { get; set; } = true;

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
