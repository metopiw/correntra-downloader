# Changelog

All notable changes to Correntra Downloader are recorded here. Dates are UTC.

## Unreleased

### Security
- **The browser bridge now accepts only the genuine Correntra extension.**
  The extension's identity is pinned via a fixed key in its manifest, so its
  ID is identical on every install, and the local HTTP bridge matches that
  exact Origin — any *other* browser extension or web page is rejected with
  403 instead of any `chrome-extension://` origin being trusted. After
  updating, reload the extension once (`chrome://extensions` → refresh icon).

### Added
- **First-run browser extension setup wizard**: the installer now ships
  `browser-extension/` next to the app and, when capture is not yet live,
  Correntra opens a guided dialog — detects Chrome/Edge/Brave, opens the
  extension folder, copies its path to the clipboard, launches
  `chrome://extensions`, and walks through "Load unpacked" in Turkish or
  English. A status dot turns green the moment the extension first reaches
  the agent. Reopenable from Settings → Browser ("Set up / repair the
  extension…").

### Fixed
- Release pipeline: `gh release upload` now enumerates files explicitly
  (PowerShell does not expand globs for external commands).
- `scripts/release.ps1` copies `browser-extension/` into staging — the
  installed app previously contained no extension files at all.

## 0.4.0 — 2026-08-25

### Added
- **Playlists expand into their own folder**: URLs like `youtube.com/…?list=…`
  now download every entry as numbered files (`001 - Title.mp4`, …) inside a
  subfolder named after the playlist, instead of collapsing to a single merged
  file. The job row shows the total size of the produced set.
- **Right-click "Download with Correntra" in the browser**: the extension adds
  a context-menu entry for links, videos and audio. Using it is an explicit
  intent, so it works even while general capture is switched off.
- **Optional VirusTotal reputation check** (Settings → Privacy): completed
  downloads can be checked against ~70 engines. Only the file's SHA-256 digest
  ever leaves the machine — never the file itself. The verdict appears under
  the file name; threats turn red.
- **Aggregate speed sparkline** in the status bar: a lightweight 60-sample
  line chart of combined transfer speed, redrawn at most twice per second.
- **Visual language pass**: consistent type scale (11/13/15/21), soft hover
  transitions on buttons and rows, and a 180 ms fade+rise entrance for dialogs.
- `CONTRIBUTING.md` documenting FSL ground rules, PR checklist, good first
  issues and security reporting.

### Fixed
- **The chosen language no longer resets on restart**: startup honours the
  language saved in Settings instead of re-deriving it from the OS culture.
- The About window now shows the real assembly version (was frozen at
  "Sürüm 0.1.0").

## 0.3.9 — 2026-08-23

### Fixed
- **Instagram/Twitter "Kalite bulunamadı" hid the real error**: when yt-dlp
  extraction failed on a watch page (login-walled reels, throttled guest
  tokens), the agent silently fell back to the manifest resolver, which
  treated the HTML page as a "direct" media file and answered accepted with
  an empty quality list. The bridge now rejects with the mapped reason:
  `media-login-required` ("Oturum gerekli — sitede giriş yapın" in the
  overlay), `media-rate-limited`, or the generic failure — verified live:
  public reels still list qualities, login-walled ones explain why not.
- **Downloads whose target file already existed failed forever at 0 bytes**
  with "The file could not be written": the engine refuses to overwrite
  (by design) but nothing picked a new name, so re-takeovers of e.g.
  `yt-dlp.exe` burned all 5 retries. The coordinator now resolves
  IDM-style "name (2).ext" collisions before starting HTTP, HLS/DASH and
  yt-dlp transfers and persists the renamed file. Verified end to end:
  takeover with `yt-dlp.exe` present completes as `yt-dlp (2).exe`,
  state 9.
- **Cookie-race losers piled up as orphaned yt-dlp processes** after each
  quality lookup; stacked clicks tripped rate limits that made even the
  anonymous probe fail. Losing probes are now killed the moment one wins.
- **Bridge hardening (security scan)**: `/ping`, `/jobs`, `/takeover`,
  `/confirm`, `/media/*` now also require a `Host` header of
  `127.0.0.1:27410`/`localhost:27410`, closing the DNS-rebinding residue
  the Origin allow-list could not cover. Scan otherwise clean: loopback
  bind, chrome-extension-only CORS, 128 KB body cap, parameterized SQL,
  `SafePath`/`HttpHeaderSet` validation at IPC boundaries, `ArgumentList`
  process spawning (no shell), current-user-only named pipes, extension
  UI confined to a closed ShadowRoot without HTML sinks.
- **Instagram feed "Liste alınamadı" but permalink worked** — on
  `instagram.com` ana sayfa the overlay sent `location.href`
  (`https://www.instagram.com/`, the feed) to yt-dlp instead of the post's
  permalink (`/reels/DcJYsBEAWd0/` — status bar'da görünen link). Feed'i
  extract edemeyince "Liste alınamadı" gösteriyordu; aynı videoya tıklayıp
  `/reels/...` sayfasına gidince `location.href` permalink olduğu için
  çalışıyordu (senin bulduğun bug, resimlerde net). `content.js` artık
  videonun kapsayan `<article>`'ındaki gerçek post linkini (`/p/`, `/reel`,
  `/reels/`, `/tv/`, `.../status/...`) bulup hem `url` hem `pageUrl` olarak
  gönderiyor; `AgentCommandDispatcher.SelectYtDlpTarget` da feed vs permalink
  arasında daha spesifik olanı tercih edecek şekilde sertleştirildi. Senin
  `DcJYsBEAWd0` reel'i ile canlı doğrulandı: feed payload `media-resolve-
  failed`, permalink payload `ACCEPTED 1280p/960p/640p`, mixed durumda da
  server doğru olanı seçiyor — artık ana sayfadan da indirilebilecek.

## 0.3.8 — 2026-08-22

### Fixed
- **Instagram / Reddit logged-out extraction** — stable yt-dlp lacks browser
  impersonation (`curl_cffi`) and Reddit's new `loid` session, so public
  reels and many Reddit videos returned "empty media response" or
  "Account authentication is required". The sidecar now tracks the
  **nightly/master build** (`yt-dlp-nightly-builds`) which bundles
  `curl_cffi` for impersonation and includes the Reddit `old.reddit`
  session fix. `scripts/get-yt-dlp.ps1` prefers the nightly URL first.

## 0.3.7 — 2026-08-22

### Fixed
- **Quality listing was slow (~10 s) on YouTube**: the cookie chain probed
  chrome → edge → firefox → anonymous sequentially, so every capture waited
  on each probe (locked profiles fail in ~1 s each). All four sources are
  now raced in parallel and the first success wins — YouTube quality lists
  drop from ~11 s to ~5 s (measured).
- **Instagram reels showed "Kalite bulunamadı" instead of the real reason**:
  Instagram now rejects anonymous metadata requests ("empty media response")
  and requires login cookies; when every cookie source fails the manifest
  fallback returned an empty list that read as "no qualities". This needs a
  signed-in browser profile: fully close Chrome (so Correntra can read its
  cookies), or sign in to instagram.com in Edge/Firefox.

## 0.3.6 — 2026-08-22

### Fixed
- **Instagram/TikTok "Kaliteler alınıyor..." → "Kalite bulunamadı"**: TikTok's
  JS challenge (`Unable to extract universal data for rehydration`) is
  flaky — the same URL fails and then succeeds. `YtDlpExecutor` now retries
  the whole cookie chain once after 1.2 s for this transient error, so the
  overlay's quality list appears instead of a generic failure. Verified on
  `spikeandred` 7608296975917681942 and the `haruncan` Instagram Reel.

## 0.3.5 — 2026-08-22

### Fixed
- **Download speed capped at ~1 MB/s (IDM uses 8, Correntra used 32)**:
  32 parallel range requests trigger `429 Too Many Requests` on many
  hosts (verified on `ash-speed.hetzner.com`: 1 segment = 3.46 MB/s,
  32 segments = 1.12 MB/s). The engine now defaults to **8 segments**
  (IDM default) and the Settings slider (`Ayarlar → Segment`) is actually
  wired — `DownloadJobCoordinator` reads `desktop-settings.json` so the
  chosen 1…32 value is honoured. No engine rewrite needed; the transfer
  core was already tuned for full bandwidth (256 KiB buffers, HTTP/2
  multiplexing, checkpoint throttling).

## 0.3.4 — 2026-08-22

### Fixed
- **Browser download takeover rebuilt from scratch (IDM-style)** — the old
  `downloads.onCreated`-only flow was fragile in MV3 (service worker could
  be reclaimed mid-ping, so `pause()` never ran and fast servers finished
  in Chrome before `cancel()` could fire). The extension now uses two
  independent layers: (1) a content-script click interceptor for obvious
  file links (`.bin/.zip/.exe/…` or `[download]`) that offers the URL to
  the agent *before* a DownloadItem exists, and (2) a hardened
  `onCreated` + `onDeterminingFilename` path that pauses first, keeps the
  worker alive with an alarm, and only resumes when Correntra truly cannot
  accept the download. The screenshot's "12 MB / 100 MB still in Chrome"
  case no longer occurs.
- Keepalive: an `alarms`-based heartbeat holds the MV3 service worker
  alive for the full takeover window; previously the worker could die
  during the 2 s ping → post window.

## 0.3.3 — 2026-08-22

### Fixed
- **Fast browser downloads escaped to Chrome before Correntra could take
  over**: the extension pinged the agent *before* pausing Chrome's download,
  so on fast servers (e.g. ash-speed.hetzner.com) the file could complete in
  the browser during that window and `cancel()` became a no-op — Chrome saved
  it while no hand-off was visible. The download is now paused first
  (IDM-style), then handed off; it is resumed only when Correntra cannot
  accept it.
- A completed-in-Chrome race is detected explicitly: an already-finished
  download is no longer re-downloaded by the agent; it is reported as
  "Chrome had already finished it" instead.
- The agent health check used a single 700 ms attempt, which cold MV3 service
  workers could miss even with the agent healthy — that silently dropped the
  takeover back to Chrome's own downloader. It now retries once with a 2 s
  budget.

### Added
- **Capture diagnostics**: every download event outcome is stored and shown
  in the popup ("✓ handed to Correntra" / "Correntra unreachable — Chrome
  kept it" / rejected / capture switch off), so a fall-through is explained
  instead of silent.
- Toolbar badge feedback: green ✓ when a download is captured, red ! when
  the browser keeps it.

## 0.3.2 — 2026-08-22

### Fixed
- **Captured downloads could surface without any visible prompt**: the save
  confirmation was modal to the main window, so a minimized or tray-hidden
  shell made it invisible. The dialog is now always-on-top, self-activating,
  and the shell restores itself before showing it.
- The capture popup now shows a live agent status dot ("Correntra çalışıyor /
  kapalı") so a download that falls through to the browser is explained
  instead of silent.

### Added
- IDM-style "Bu kategori için bu yolu hatırla": confirmed folders are stored
  per category and preselect on every future capture dialog.
- Compact IDM-like capture dialog layout with IDM wording
  ("İndirmeyi başlat" / "Daha sonra indir").

## 0.3.1 — 2026-08-22

### Fixed
- Media jobs from the extension died at 0% with "unable to download video
  data: HTTP Error 403": the bundled yt-dlp sidecar had gone stale and
  YouTube rejects its anonymous stream URLs. The vendored binary is refreshed
  (2026.08.19); refresh it again any time with `scripts/get-yt-dlp.ps1 -Force`.
- The cookie pass no longer depends on Chrome alone. Chrome keeps its cookie
  database exclusively locked while running (yt-dlp #7271), which made the
  first pass fail on most sessions; enumeration and downloads now fall back
  through edge → firefox before the anonymous attempt.
- Bot-check / age-gate / HTTP 403 rejections surface an actionable failure
  message instead of a bare transport error.
- **Progress bar froze mid-way (e.g. "%66") while the file was already
  usable**: yt-dlp restarts its percentage at zero for every video/audio
  track, so the UI read raw in-track numbers. Progress is now cumulative and
  monotonic across all tracks, and merge/remux moves the row to a distinct
  "Birleştiriliyor / Finalizing…" state instead of pretending to still
  download.

### Added
- Update checking now works outside setup installs: the latest GitHub
  release is compared against the app version (anonymous API first, the
  locally authenticated `gh` CLI as fallback for private repositories) and a
  small bottom-right toast offers to install. Velopack self-update is used
  when the app is setup-installed; portable runs open the release page.
- Settings persist across restarts (`%AppData%\Correntra\Downloader\
  desktop-settings.json`). Previously only theme and language survived a
  relaunch; every other option silently reset.
- "Şimdi denetle" button is wired up and reports the check result inline;
  the settings page shows the real assembly version instead of a hardcoded
  string.
- Category extension lists expanded (video: flv/f4v/ogv/asf/vob/divx/mxf,
  music: mka/amr, compressed: zst/lz4/cab/jar/br, documents:
  odp/ods/docm/xlsm/xlsb/xps/fb2/djvu, programs: msu/msp/appinstaller).
- `scripts/test-media-e2e.ps1`: posts a real yt-dlp-backed job over the
  loopback bridge (`media/start` → `confirm` → poll) so the "download never
  starts" class of bugs is reproducible end to end.

### Removed
- The "Güncelleme kanalı" dropdown. Stable vs pre-release is already covered
  by the plain-language "Ön sürümleri göster" switch; normal users should not
  have to pick a channel.

## 0.3.0 — 2026-08-19

### Added
- **Correntra Catch** browser extension (`browser-extension/`): Manifest V3,
  Chrome/Edge unpacked load. A thin IDM-style bar sits on the **video frame**
  (top-right), at 50% opacity until hovered. Clicking it lists qualities
  highest-first; choosing one opens Correntra's save confirmation.
- Loopback bridge media routes: `POST /media/resolve`, `POST /media/start`
  (plus CORS for `chrome-extension://` origins, including private-network
  preflight). `scripts/test-bridge.ps1` still covers takeover.
- Broader site extraction: yt-dlp host list expanded, unknown watch pages
  try the extractor, and jobs with `X-Correntra-Format` always run through
  yt-dlp so a generic site cannot save HTML. Direct `.mp4`/`.m3u8` files on
  unrelated hosts still use the HTTP/HLS engines.
- Network sniffing in the extension for generic players (HLS/DASH/progressive)
  when the `<video>` element only exposes a blob URL.

### Changed
- Browser session cookies stay in the existing yt-dlp
  `--cookies-from-browser chrome` path (anonymous fallback unchanged). The
  extension does not copy cookie values into logs or extension storage.

## 0.2.1 — 2026-08-18

### Removed
- **Bundled browser extensions deleted** (`browser-extension/`,
  `browser-extension-v2/`, backups) together with their build/test/release
  wiring and the Native Messaging auto-registration. The product is now a
  standalone desktop app.

### Changed
- The agent's loopback HTTP bridge (`http://127.0.0.1:27410/`: `/ping`,
  `/jobs`, `/takeover`, `/confirm`) is the single documented seam for any
  future browser extension; `AGENTS.md` and `README.md` describe it and ban
  native messaging for new integrations.

## 0.2.0 — 2026-08-16

### Added
- **Social video engine (yt-dlp)**: YouTube, Facebook, X/Twitter, Instagram,
  TikTok, Twitch, Vimeo, Reddit, Dailymotion, Tumblr and SoundCloud pages are
  now extracted through a bundled open-source yt-dlp sidecar
  (`scripts/get-yt-dlp.ps1`, Unlicense). The engine merges separate
  video/audio tracks with the shipped FFmpeg and remuxes to the expected
  container.
- **IDM-style capture bar on social sites**: the browser extension now shows a
  single compact "Download video" bar over playing videos on social platforms.
  Clicking it opens a quality list (144p…4K, plus an audio-only option) before
  anything is queued; nothing downloads silently.
- **Quality selection**: raw video+audio candidate pairs collapse into one
  video row; the audio track is offered inside the quality list instead of a
  second bar.
- **Speed & ETA columns**: the main window now shows a live transfer rate and
  remaining time per download, computed from windowed agent snapshots; the
  aggregate speed in the status bar is real as well.
- **Double-click opens file**: double-clicking a finished row opens the file
  (same as right-click → Open file).
- `scripts/get-yt-dlp.ps1` fetches and verifies the yt-dlp sidecar;
  `dev-start.ps1` and `release.ps1` deploy yt-dlp/FFmpeg next to the binaries.
- **Browser session forwarding**: a persistent popup setting ("Use browser
  session for media downloads") forwards site cookies/User-Agent to yt-dlp,
  defeating bot checks on YouTube/Facebook/Instagram. Falls back to
  `--cookies-from-browser` and then to anonymous extraction.
- **Correntra Catch v2 extension** (`browser-extension-v2/`): rebuilt from
  scratch, build-free plain JS, amber identity, single capture pill with a
  quality picker (plus audio-only option). The v1 extension is preserved in
  `browser-extension-backup/` as a rollback.
- Native host registration now accepts sideloaded development IDs
  (`chrome-extension://*/` plus explicit IDs) so unpacked extensions always
  reach the host; `Correntra.NativeHost.exe --register --dev` re-applies it.
- `AGENTS.md` with repository guidance for coding agents.

### Fixed
- **Quality list never reached the browser**: the native messaging host dropped
  the agent's `mediaQualities` payload while relaying responses, and aborted
  media commands after a 900 ms handshake budget. The host now forwards
  qualities/job IDs and gives `media.resolve`/`media.start` 30 s/10 s budgets,
  so clicking the capture bar really opens the quality picker.
- Page-capture requests used a `candidateId` the native host validator rejects
  (`"page"`); they now send a spec-shaped opaque ID, so media jobs are created.
- Downloads no longer die on transient network drops: `TaskCanceledException`,
  `EndOfStreamException` and `TimeoutException` are retried per segment, a
  90-second stall timeout reconnects hung streams, and failed jobs are
  automatically re-queued (up to 5 backoff retries) while the checkpoint file
  preserves progress for true resume.
- **0% stall fixes**: the resource probe now has a 60 s budget and at most 3
  fast retries (previously an unresponsive server parked jobs at 0% for
  minutes), header reads on ranged requests get the stall timeout, and the
  automatic retry path actually re-queues Downloading jobs (the old code
  called a transition that only worked from Failed/Cancelled).
- **Real file names**: generic names like `download` are replaced before the
  transfer starts using the server's Content-Disposition / final redirect URL,
  falling back to a MIME-type → extension map; takeover offers also fall back
  to the original page URL when CDN redirects strip the extension.
- Takeover downloads from sites like mega.nz no longer end up named
  `download` without an extension; the real name is derived from the URL when
  Chrome reports a placeholder.
- Job deletion no longer crashes when the destination directory is missing.
- yt-dlp failures now surface the actual (redacted) engine error in the UI
  instead of a generic message.

### Changed
- **Extension v2.1 — video capture removed by user decision; browser download
  takeover kept.** The extension now talks to the agent over a loopback HTTP
  bridge (`http://127.0.0.1:27410`, IDM-style) instead of native messaging:
  `POST /takeover`, `POST /confirm`, `GET /jobs`, `GET /ping`. The bridge is
  loopback-only, rejects web-page origins, and is testable with plain curl
  (`tmp/bridge-test.ps1` proves takeover → confirm → Completed with the
  server-derived file name). Native messaging is no longer used by the
  extension; social-video downloads remain available through the desktop
  app's own flows.
- Transfer defaults tuned for full-bandwidth downloads: up to 32 parallel
  segments, HTTP/2 multiplexing (`EnableMultipleHttp2Connections`), 256 KiB
  buffers, 8 retry attempts with longer backoff.
- Progress persistence is throttled and non-blocking so SQLite writes never
  stall the transfer thread.

## 0.1.0 — 2026-08-15

- Initial public development snapshot: segmented HTTP transfer engine with
  durable checkpoints, Avalonia desktop shell, background agent, native
  messaging host, HLS/DASH remuxing via an LGPL FFmpeg sidecar, and the
  Chrome/Edge capture extension.
