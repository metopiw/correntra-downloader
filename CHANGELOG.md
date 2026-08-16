# Changelog

All notable changes to Correntra Downloader are recorded here. Dates are UTC.

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
