# Jellyfin M3U8 Downloader

A Jellyfin server plugin that downloads HLS (`.m3u8`) streams into `.mkv` files.

- **Bulk add** — paste many URLs at once, each with the output filename you want.
- **Sequential queue** — exactly one download runs at a time.
- **Survives restarts** — the queue is persisted; a download interrupted by a server restart is
  automatically re-queued.
- **Retries** — failed downloads are retried a configurable number of times before being parked in
  a `Failed` state you can requeue by hand.
- **No extra dependencies** — uses the `ffmpeg` binary Jellyfin already ships with.

Targets **Jellyfin 10.11.x** (`net9.0`), verified against 10.11.11.

## Building

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0), or Docker (see below).

```bash
dotnet build -c Release
```

```bash
dotnet test
```

## Installing from the plugin repository

Once a release has been published, point Jellyfin at the manifest and it will offer the plugin —
and every later version — from the Dashboard, no manual file copying:

1. Dashboard → Plugins → Repositories → **+**
2. Name: `M3U8 Downloader`, URL:
   `https://github.com/OWNER/REPO/raw/main/manifest.json`
3. Dashboard → Plugins → Catalog → install **M3U8 Downloader**, then restart Jellyfin.

Jellyfin polls that manifest, so new releases show up as available updates on their own.
Replace `OWNER/REPO` with this repository's path.

## Releasing

Releases are driven by [Conventional Commits](https://www.conventionalcommits.org/) — there is no
version to bump by hand. Merge to `main` and the **Release** workflow reads every commit since the
last `v*` tag:

| Commit | Bump |
| --- | --- |
| `fix: ...`, `perf: ...`, `revert: ...` | patch — `1.2.3` → `1.2.4` |
| `feat: ...` | minor — `1.2.3` → `1.3.0` |
| `feat!: ...` or a `BREAKING CHANGE:` footer | major — `1.2.3` → `2.0.0` |
| `docs`, `chore`, `ci`, `test`, `style`, `refactor` | no release |

If nothing releasable landed, the workflow reports why and stops. Otherwise it stamps the version
into the `.csproj` and `build.yaml`, builds, runs the tests, zips the DLL, tags `vX.Y.Z`, publishes a
GitHub release with the zip and its MD5, and commits the version into `manifest.json` on `main` — so
existing Jellyfin installs see the update on their next poll. A changelog is generated from the
commit subjects and used for both the release notes and the manifest entry.

To force a release with no releasable commits (or a larger bump than the commits imply), run the
**Release** workflow from the Actions tab and pick `patch`/`minor`/`major`.

CI rejects a pull request whose commits are not Conventional Commits, since a malformed subject
would silently drop out of the version calculation.

Plugin metadata in the manifest (name, description, GUID, `targetAbi`) comes from `build.yaml` —
edit it there, not in `manifest.json`. The `version:` line in `build.yaml` is overwritten at release
time and is only the fallback starting point before the first tag exists.

## Installing on a server

The plugin builds to architecture-independent IL, so **you do not need .NET on the server** — build
on your workstation and copy one file up.

### 1. Check the server's Jellyfin version

The plugin targets ABI `10.11.0.0` and is built against `Jellyfin.Controller` 10.11.11.
It will not load on 10.10 or earlier — those need a build against the matching 10.10 packages.

```bash
curl -s http://YOUR_SERVER:8096/System/Info/Public | grep -o '"Version":"[^"]*"'
```

### 2. Build and copy the DLL

```bash
dotnet publish Jellyfin.Plugin.M3u8Downloader -c Release -o ./artifacts
```

No local .NET 9 SDK? Build in a container instead — same output, and it needs nothing installed:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet publish Jellyfin.Plugin.M3u8Downloader -c Release -o /src/artifacts
```

Then copy it up:

```bash
scp ./artifacts/Jellyfin.Plugin.M3u8Downloader.dll YOUR_USER@YOUR_SERVER:/tmp/
```

Only the `.dll` is needed. The `.pdb`, `.xml` and `.deps.json` beside it are debug symbols and
documentation — Jellyfin ignores them.

### 3. Find the Jellyfin config directory

Run this on the server; it tells you which kind of install you have.

```bash
docker ps --filter ancestor=jellyfin/jellyfin --format '{{.Names}}' || systemctl status jellyfin --no-pager | head -3
```

**Docker** — find the host path bound to `/config`:

```bash
docker inspect YOUR_CONTAINER --format '{{range .Mounts}}{{.Destination}} -> {{.Source}}{{"\n"}}{{end}}'
```

**Native package** — the config directory is normally `/var/lib/jellyfin`:

```bash
systemctl show jellyfin -p ExecStart | tr ' ' '\n' | grep -i datadir
```

### 4. Install the plugin

With `JELLYFIN_CONFIG` set to the directory from the previous step:

```bash
sudo mkdir -p "$JELLYFIN_CONFIG/plugins/M3u8Downloader_1.0.0.0"
sudo cp /tmp/Jellyfin.Plugin.M3u8Downloader.dll "$JELLYFIN_CONFIG/plugins/M3u8Downloader_1.0.0.0/"
sudo chown -R jellyfin:jellyfin "$JELLYFIN_CONFIG/plugins/M3u8Downloader_1.0.0.0"
```

For Docker, the files are written on the host but read by the container — match the ownership
Jellyfin runs as inside it (often `uid 1000`) rather than a host `jellyfin` user:

```bash
sudo chown -R 1000:1000 "$JELLYFIN_CONFIG/plugins/M3u8Downloader_1.0.0.0"
```

### 5. Restart and confirm it loaded

```bash
sudo systemctl restart jellyfin    # or: docker restart YOUR_CONTAINER
```

```bash
sudo journalctl -u jellyfin -n 200 --no-pager | grep -i m3u8    # or: docker logs YOUR_CONTAINER 2>&1 | grep -i m3u8
```

You want two lines — the assembly loading, and the worker starting:

```
PluginManager: Loaded plugin: M3U8 Downloader 1.0.0.0
QueueWorker: M3U8 download queue worker started
```

It then appears under **Dashboard → Plugins**.

### 6. Point it at a writable output directory

Dashboard → Plugins → **M3U8 Downloader** → **Settings** tab → *Output directory*. Two things
to get right:

- The directory must be **writable by the user Jellyfin runs as**, or every job fails with a
  permission error. `sudo -u jellyfin test -w /path && echo ok` checks it.
- **On Docker, this is a path inside the container.** Use a destination that is bind-mounted to the
  host (`/media`, `/downloads`, …) — anything else is written into the container's writable layer
  and is lost the moment the container is recreated. `docker inspect` from step 3 lists the mounts.

Finally, add that folder as a Jellyfin library so finished downloads are scanned in. The plugin
notifies the server about each new file, but Jellyfin can only pick it up if some library covers
the path.

### Where its data lives

The queue is persisted at `$JELLYFIN_CONFIG/plugins/Jellyfin.Plugin.M3u8Downloader/queue.json`,
separate from the plugin DLL. Back it up, or delete it to reset the queue while Jellyfin is
stopped.

### Upgrading

Copy the new DLL over the old one in the same folder and restart. To roll back, replace the DLL
with the previous build — the queue file format is unchanged.

## Usage

Dashboard → Plugins → **M3U8 Downloader**. The page has two tabs: **Downloads** (the queue and
the bulk-add box) and **Settings**.

Set an **output directory** on the Settings tab first; the queue refuses to run jobs until one is
configured, rather than guessing a location and writing media somewhere unexpected. Until you do,
the Downloads tab shows a banner saying so, with a shortcut to the field.

Optionally set a **destination folder** for the batch — everything you add is written under it,
relative to the output directory, so a season's worth of episodes does not need the path repeated on
every line. It is remembered between batches.

Then paste jobs into the box on the Downloads tab, one per line:

```
https://example.com/stream.m3u8 | My Show/Season 01/S01E01
https://example.com/other.m3u8 | Some Movie (2019)
# comments and blank lines are ignored

https://example.com/derives-a-name-from-the-url.m3u8
```

- The part after `|` is the output name and is optional; without it, a name is derived from the URL.
- The `.mkv` extension is added automatically (and any other extension is replaced).
- Subfolders are allowed and are created as needed. Names are always resolved inside the configured
  output directory: `..` segments are stripped (so `../../etc/x` lands at `<output>/etc/x.mkv`) and
  absolute paths are rejected outright.
- An existing file is never overwritten; ` (2)`, ` (3)`, … is appended instead. A download already
  in flight counts as taken, so two jobs queued under the same name do not collide.
- A name that cannot be written at all — or an unset output directory — is reported inline as you
  add it, rather than becoming a job that fails identically on every retry.

A partially valid paste is not rejected wholesale: the valid lines are queued and the rejected ones
are listed underneath with the reason.

### The queue

Each entry shows a status icon, the output name, and a progress bar while downloading — with the
percentage, elapsed/total time, the speed ffmpeg reports (`12.4x` means twelve times faster than
real time) and an estimate of the time left. A live stream has no duration to measure against, so
its bar tracks the **Maximum length** cap instead when one is set, and sweeps when there is no cap
at all. Failures print ffmpeg's own output in a selectable block underneath. A row of counts above
the list summarises the queue, and a filter narrows it to **Active**, **Failed**, or **Completed**.

The list refreshes in place — every 2 seconds while anything is queued or downloading, every 10
when the queue is idle, and not at all while the browser tab is in the background. Updating in
place rather than rebuilding is what keeps buttons clickable and stops text you are selecting in an
error block from being wiped out from under you.

A completed entry also shows the path the file was actually written to, which is not always the
name that was asked for — see the deduplication rules above.

Each entry offers the actions that apply to its state: **Cancel** while downloading, **Download
this next** / **Download this last** and **Remove** while queued, **Retry** and **Remove** once
failed or cancelled, **Remove** and **Copy output path** once completed, and **Copy source URL**
always. Cancel and Remove
ask for confirmation; removing an entry never deletes the file it downloaded, and cancelling
discards only the partial `.part` file. Retrying resets the attempt counter and puts the job back at
the end of the queue. **Retry all failed** does the same for every failed and cancelled job at once,
and shows how many that is.

**Pause queue** stops anything new from being claimed. Downloads already running are left to
finish — a partial HLS download cannot be resumed later, so stopping one is a cancellation, and that
stays a deliberate choice. The queue stays paused across restarts until you resume it.

The list is ordered the way the queue will actually run: downloading first, then pending in queue
order, then finished entries newest-first.

### Settings

On the **Settings** tab. The last five are tucked behind **Advanced**, since they are rarely needed.
Everything except the output directory has a working default.

| Setting | Default | Notes |
| --- | --- | --- |
| Output directory | *(empty)* | Required. Absolute path on the server. |
| Simultaneous downloads | 1 | How many downloads run at once, up to 8. Each is its own ffmpeg process on the same disk. |
| Simultaneous downloads per site | 1 | Applied on top of the limit above. See *Avoiding an IP block*. |
| Speed limit | 10× realtime | Paces the fetch instead of pulling a stream flat out. `0` is no limit. See *Avoiding an IP block*. |
| Pause between downloads | 5s | Gap before starting another download from the same site. Other sites are unaffected. |
| Pause after being rate-limited | 30 min | How long every download from a site waits after it answers `429` or `403`. |
| Maximum length | 0 (no limit) | Stops a download after this many minutes. Applies only to sources whose length ffmpeg cannot determine — the escape hatch for live streams, which never end on their own. |
| Apply the maximum length to every download | off | Also caps sources of known length, truncating them. Off by default so a measured three-hour film is never silently cut short. |
| Keep finished jobs for | 0 (forever) | Days of history to keep. Finished entries older than this are dropped from the list; files are never deleted. |
| Max retries | 2 | Attempts after the first failure. |
| Retry delay | 30s | Wait before a retry. |
| Stall timeout | 10 min | Kills a download that has made no progress at all for this long and retries it. 0 disables it. |
| User-Agent | *(empty)* | Some hosts require a browser-like value. |
| Referer | *(empty)* | Some hosts reject requests without one. |
| HTTP proxy | *(empty)* | Routes downloads through a proxy, e.g. `http://10.0.0.5:8080` or `http://user:pass@10.0.0.5:8080`. Applies to this plugin's downloads only, not to the rest of Jellyfin. Must be an `http://` URL even for https streams — ffmpeg has no SOCKS support. |
| Proxy bypass list | *(empty)* | Comma-separated hosts that skip the proxy, e.g. `localhost,127.0.0.1,.lan`. |
| Skip damaged packets | on | Drops the partial frame a truncated segment leaves behind, so the download finishes with a brief glitch instead of failing. See *Troubleshooting*. |
| Reuse HTTP connections | **on** | Leave on — see *Avoiding an IP block*. Turn it off only for a CDN that rotates hostnames per segment. |
| Extra ffmpeg **input** arguments | *(empty)* | Placed before `-i`. Reconnect/HLS options only work here. |
| Extra ffmpeg **output** arguments | *(empty)* | Inserted before the output path. Quote values with spaces. |
| Notify library after download | on | Points Jellyfin at the finished file so it appears without a manual scan. |

## Troubleshooting

### The site blocked my IP

A single download can generate traffic that looks exactly like a scraper. Four separate behaviours
used to stack up, and the defaults now guard against all of them:

**One download used to fetch every quality at once.** An HLS master playlist lists each bitrate as
its own video stream. The plugin picks the best one from what `ffprobe` reports — but when the probe
failed, the fallback selected *every* video stream, and ffmpeg's HLS demuxer then downloaded four to
six variant playlists **simultaneously** from the same host. That alone is enough to trip a limiter.
A failed probe on an HLS source now maps nothing at all and lets ffmpeg choose a single video track.
The cost is that extra audio tracks and subtitles are not kept in that case; the probe succeeding is
the normal path and is unaffected.

**Every segment used to open a new connection.** `Reuse HTTP connections` now defaults to **on**.
Without it, a two-hour stream means roughly 1200 fresh TCP and TLS handshakes, and connection rate is
one of the first things a rate limiter counts.

**Nothing paced the fetch.** `Speed limit` now defaults to 10× realtime, so a two-hour stream takes
about twelve minutes as a steady trickle rather than arriving in two minutes as one dense burst.
Drop it to `2` or `3` for a site that has already blocked you.

**A rate limit used to trigger more requests.** A `429` or `403` now pauses *every* queued download
from that site for 30 minutes, rather than retrying in 30 seconds. This matters because a download
cannot resume part-way — the retry re-requests the whole stream from the first segment, while the
site is still refusing you, which is how a temporary throttle becomes a lasting block.

If you are upgrading an existing installation, open the plugin settings and **tick "Reuse HTTP
connections" by hand**. The new default only applies to fresh installs: your saved configuration
already has it off, and nothing can tell that apart from a deliberate choice.

If a site blocks you anyway, in rough order of effect: lower the speed limit to `2`, raise the pause
between downloads, and set a browser-like **User-Agent** and a **Referer**.

If the block is by IP rather than by behaviour, set an **HTTP proxy** in Advanced settings. It
applies to this plugin's downloads only, so the rest of the server keeps its own connection.

> The speed limit uses ffmpeg's `-readrate`, which needs ffmpeg 5.1 or newer. Every Jellyfin 10.9+
> bundle has it. If your server uses an older ffmpeg and downloads fail with `Option not found`, set
> the speed limit to `0`.

### `Cannot reuse HTTP connection for different host` / `Stream ends prematurely`

```
[https] Cannot reuse HTTP connection for different host: ...ams-static-03... != ...ams-static-14...
[https] Stream ends prematurely at 511739, should be 574904
[aac_adtstoasc] Error parsing ADTS frame header!
[out#0/matroska] Error muxing a packet
```

One failure, read bottom-up. The CDN serves consecutive segments from rotating hostnames; ffmpeg's
persistent connection cannot follow that, the segment download is cut short, the truncated AAC data
fails the `aac_adtstoasc` bitstream filter that Matroska requires, and the mux aborts. The ADTS and
muxing errors are symptoms — the cause is the truncated segment.

The plugin now defends against this by default, because every relevant ffmpeg option is off out of
the box:

| Option | ffmpeg default | Why it matters |
| --- | --- | --- |
| `-reconnect`, `-reconnect_streamed`, `-reconnect_on_network_error` | **off** | A dropped TLS connection mid-segment ends the download instead of retrying. |
| `-seg_max_retry` | **0** | A single truncated segment aborts everything rather than being re-fetched. |
| `-http_persistent` | **on** | Connection reuse is what breaks on rotating-hostname CDNs. Left alone unless you turn **Reuse HTTP connections** off. |
| `-fflags +discardcorrupt`, `-err_detect ignore_err` | **off** | The partial frame at the end of a truncated segment reaches the muxer and kills the download. |

The first three make ffmpeg re-fetch a truncated segment. **Skip damaged packets** is the last
resort for when re-fetching does not help — some hosts truncate the same segment on every attempt,
and retrying only reproduces the failure. With it on, the damaged packets are discarded and the
rest of the stream is written: you get a file with a brief glitch where each truncated segment
ends, instead of no file at all. Turn it off if you would rather such a download failed loudly.

That still leaves one gap, and the plugin closes it automatically. `+discardcorrupt` can only drop
packets the demuxer *flagged* as damaged, and a segment cut at a TLS record boundary often arrives
structurally valid — so nothing is flagged, the partial ADTS frame reaches `aac_adtstoasc` anyway,
and the mux dies at the same point on every retry. When a download fails with `aac_adtstoasc`
followed by `Error applying bitstream filters`, the plugin retries it once with the audio
re-encoded (`-c:a aac`) instead of copied. That takes the bitstream filter out of the path
entirely: the decoder skips what it cannot parse and the encoder emits well-formed frames. Video
and subtitles are still copied, so the cost is the audio re-encode and a second pass over the
stream. The fallback is skipped when the failure looks like a rate limit, since re-encoding cannot
help a host that is refusing you.

If it still fails, try in **Extra ffmpeg input arguments**:

```
-rw_timeout 30000000 -reconnect_delay_max 120
```

These options must go in the **input** box. ffmpeg only honours them before `-i`, so the output
arguments box cannot set them — that is why the two are separate settings.

### A download sits at the same percentage forever

It is killed and retried after the **Stall timeout** (10 minutes by default), and the failure says
so. The plugin turns ffmpeg's reconnect options on by default, which is what keeps a flaky host
from ending a download — but it also means a host that goes silent for good is reconnected to
indefinitely rather than failing, and the job holds its queue slot the whole time. At the default
of one simultaneous download, that is the entire queue.

Raise the timeout if you genuinely have sources that go minutes between segments; set it to 0 to
restore the old behaviour of waiting forever.

### Downloads are unusually slow

First check the **Speed limit**. It defaults to 10× realtime, so a two-hour stream takes about
twelve minutes on purpose — see *The site blocked my IP* for why. Raise it, or set it to `0` for no
limit, if the site tolerates that.

Then check that **Reuse HTTP connections** is on. It should be by default, but an installation
upgraded from an older version keeps its saved `off`, which costs a TLS handshake per segment. If
the `Cannot reuse HTTP connection` error appears after turning it on, that CDN rotates hostnames and
you have to leave it off.

### The queue table is frozen / buttons do nothing

Fixed in the current build. Earlier versions started the 2-second poll only from Jellyfin's
`pageshow` event and stopped it on `viewhide`; for an embedded plugin page those fire
unreliably, so the table rendered once and then never updated. A retried job appeared stuck on
its old status and its **Retry** button never came back without a full page reload.

The poll now starts unconditionally when the page script runs and stops by checking whether the
page is still in the document, which no lifecycle event can get wrong. If you upgraded in place,
**hard-reload the dashboard** (Ctrl/Cmd+Shift+R) — the browser caches the plugin's config page.

### Every job fails with `Cannot start process because a file name has not been provided`

The server has not reported an ffmpeg path. Check **Dashboard → Playback → FFmpeg path**. The
plugin waits for this at startup, so this should only appear if ffmpeg is genuinely misconfigured.

### Jobs fail immediately with a permission error

The output directory is not writable by the user Jellyfin runs as. See step 6 of the install.


## How it works

Downloads run as `ffmpeg -i <url> -c copy -dn -f matroska` — a **remux**, not a re-encode, so it is
fast and lossless. ffprobe is asked for the duration up front to drive the progress percentage; a
stream with no duration (a live stream) simply reports elapsed time instead, and **Maximum length**
is what stops one.

The probe is sent the same User-Agent, Referer and proxy as the download. A host that gates on those would
otherwise reject the probe while accepting the download — and a failed probe is not merely cosmetic,
because without a program to map, stream selection falls back to copying *every* bitrate rendition
(see below).

### Stream selection

The aim is every real audio track and subtitle language, but only one copy of the video.

An HLS **master** playlist exposes each bitrate rendition as its own video stream while sharing the
audio and subtitle tracks, and groups each rendition with those shared tracks into a *program*. So
when the source has more than one program, the plugin maps the program holding the
highest-resolution video (`-map 0:p:<n> -dn`). Otherwise — a plain media playlist, or a non-HLS
input — it maps every stream by type (`-map 0:v? -map 0:a? -map 0:s?`).

There is a third case. When `ffprobe` fails outright, nothing is known about the structure, and
mapping by type would then be actively harmful on a master playlist: `0:v?` selects every bitrate
rendition, and ffmpeg's HLS demuxer fetches all of them at once — which reads as a scraper and gets
your IP blocked. So a failed probe on an HLS source maps **nothing**, leaving ffmpeg's own default
selection to take one video, one audio and one subtitle. That gives up the extra audio tracks, and
it is the right trade: a probe that just failed is exactly when the host is already unhappy with
you.

Measured on Apple's test stream (10s sample), this matters:

| Selection | Size | Video | Audio | Subtitles |
| --- | --- | --- | --- | --- |
| `-map 0` (everything) | 5.2 MB | 5 duplicate renditions | 7 (6 duplicates) | 8 |
| ffmpeg default (no `-map`) | 2.1 MB | 1 | 1 | 1 |
| **best program** (what this does) | **2.1 MB** | **1** | **2** | **8** |

`-dn` is required in the program case because a program also contains the timed-metadata (ID3)
streams HLS carries, which Matroska cannot store — without it, muxing fails outright with
`Only audio, video, and subtitles are supported for Matroska`. It is not needed in the by-type
case, since naming the types explicitly cannot select a data stream.

To override any of this, use **Extra ffmpeg arguments** — any `-map` you supply there is added
alongside the plugin's own.

### Running more than one at a time

**Simultaneous downloads** is 1 by default, which is how the plugin has always behaved. Raising it
starts that many ffmpeg processes at once; because each writes to the same disk, more is not
reliably faster.

**Simultaneous downloads per site** is applied on top of that and defaults to 1. Several downloads
at once is ordinary when spread across different sites, and is what gets your IP blocked when aimed
at one. A site that is already at its limit does not stall the queue — the worker walks past its
jobs in queue order and claims the next one that is runnable.

Output names are claimed before the download starts rather than when ffmpeg opens the file: the
probe alone can take a minute, which is long enough for a second job with the same name to pick the
same path. The empty `.part` file is the claim, and it is visible to the next job's deduplication.

> **Subtitles and the bundled ffmpeg.** Jellyfin's bundled ffmpeg does not surface subtitle
> renditions from a *master* playlist. Measured on Apple's test stream, both 10.10's ffmpeg 7.0.2
> and 10.11's ffmpeg 7.1.4 report **17 streams with no subtitles**, where ffmpeg 9.0.1 reports 25
> including 8 WebVTT tracks. WebVTT itself is fully supported in those builds — they read a
> subtitle rendition playlist directly without trouble — so this is an HLS demuxer limitation, not
> a missing codec, and nothing the plugin does can map streams ffmpeg never exposes.
>
> The plugin already maps everything ffmpeg exposes, so subtitles will start coming through on
> their own once Jellyfin bundles a newer ffmpeg. **Audio tracks are unaffected and all preserved
> today.** To get subtitles sooner, the source's subtitle renditions have to be fetched as
> separate inputs and muxed in — see the note in the issue tracker.

Each download is written to a `.part` file and moved into place only on success, so a partial file
is never picked up by a library scan and a failure leaves nothing behind.

## API

All endpoints require an administrator token.

| Method | Route | |
| --- | --- | --- |
| `GET` | `/Plugins/M3u8Downloader/Jobs` | List all jobs. A completed job carries `ResolvedPath`, where the file was actually written |
| `POST` | `/Plugins/M3u8Downloader/Jobs` | Body `{"Text": "url \| name\n...", "Folder": "optional/subfolder"}` |
| `DELETE` | `/Plugins/M3u8Downloader/Jobs/{id}` | Cancel if running, otherwise remove |
| `POST` | `/Plugins/M3u8Downloader/Jobs/{id}/Retry` | Requeue a failed or cancelled job |
| `POST` | `/Plugins/M3u8Downloader/Jobs/{id}/MoveToTop` | Move a queued job to the front |
| `POST` | `/Plugins/M3u8Downloader/Jobs/{id}/MoveToBottom` | Move a queued job to the back |
| `POST` | `/Plugins/M3u8Downloader/Jobs/Clear` | Drop all finished jobs |

## Notes

- Resuming a partial download where it left off is not supported — HLS remuxing cannot reliably
  resume, so a retry restarts the file.
- A job that fails for a reason a retry cannot change — a rejected filename, or no output directory
  — is failed immediately rather than retried on a schedule.
- The queue survives a restart. A job interrupted by a server shutdown goes back to **Queued**
  without consuming a retry. If the server was killed rather than shut down, the `.part` file it
  left behind is swept at startup, so the retry writes to the original name instead of ` (2)`.
- If something else creates the output file while a download is running, the finished download is
  published under the next free name rather than being discarded.
- A download that finishes but cannot be moved onto its final name at all — a directory that has
  become unwritable, say — leaves the complete file in place under its `.part` name and logs where
  it is, rather than deleting hours of work over a failed rename. Rename it by hand to keep it: the
  startup sweep treats a leftover `.part` as debris and removes it on the next restart.
- DRM-protected streams are not supported.

## Limitations

Jobs are admin-only by design: the server fetches whatever URL is submitted, so this must not be
exposed to ordinary users.
