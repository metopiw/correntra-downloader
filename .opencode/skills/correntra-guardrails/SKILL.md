---
name: correntra-guardrails
description: Project checklist for Correntra Downloader — IDM-class transfer, browser bridge, media/yt-dlp and licensing gates
license: MIT
compatibility: opencode
metadata:
  audience: maintainers
  workflow: correntra
---

## What I do
- Enforce the project's non-negotiables before any code leaves the machine.

## When to use me
- On every edit in this repo — treat me as Definition of Done.

## Checklist

### 1) Transfer engine
- `MaxSegments` is **8** (IDM default). Never hardcode 32. The UI slider `SegmentsPerDownload` (1..32) in `desktop-settings.json` must be read by `DownloadJobCoordinator` — test with `ash-speed.hetzner.com`: 1 seg ~3.46 MB/s, 32 seg ~1.12 MB/s (429 storm).
- Buffers 256 KiB, `EnableMultipleHttp2Connections=true`, checkpoint throttling stays. `UnlimitedBandwidthLimiter` unless user sets a limit.

### 2) Browser → Agent bridge
- Loopback only: `http://127.0.0.1:27410` (`/ping`, `/jobs`, `/takeover`, `/confirm`, `/media/resolve`, `/media/start`). CORS only for `chrome-extension://`, with `Access-Control-Allow-Private-Network`.
- **Never reintroduce native messaging.**
- Extension is dual-layer since 0.3.4: (1) content click interceptor for `.bin/.zip/.exe/[download]` via `correntra.takeoverUrl`, (2) `onCreated` + `onDeterminingFilename` that **pauses first** and keeps MV3 alive with `alarms`. `downloads` + `alarms` permissions required.

### 3) Media / yt-dlp
- Social hosts go through `YtDlpExecutor`; plain HTTP would only save HTML.
- Cookie chain is `chrome → edge → firefox → anonymous` (Chrome locks DB while running). FFmpeg via `--ffmpeg-location`, LGPL only.

### 4) Licensing
- FFmpeg LGPL-only, yt-dlp Unlicense. Reject `--enable-gpl/nonfree`, GPL/AGPL/SSPL.

### 5) Definition of Done
1. `dotnet build` 0 warnings, all tests green
2. `scripts/test-bridge.ps1` → `download` renamed (e.g. `yt-dlp.exe`), state 9
3. `CHANGELOG.md` (and `THIRD-PARTY-NOTICES.md` if needed) updated
4. `dev-start.ps1` restart + remind to reload unpacked extension
