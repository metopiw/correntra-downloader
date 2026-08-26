# AGENTS.md — guidance for coding agents in this repository

> **Communication:** Keep answers short (2-4 lines) unless user asks for detail. No code dumps unless requested.

> **Reading order for a new agent:** this file → `docs/DECISIONS.md` (why
> things are the way they are; never re-litigate a settled decision) →
> `CHANGELOG.md` (what changed recently). The maintainer does not read code;
> explain outcomes, not implementation.

Correntra is an IDM-class download manager: Avalonia desktop shell
(`src/Correntra.Desktop`), a background transfer agent
(`src/Correntra.Agent`), a segmented HTTP engine (`src/Correntra.Transfer`),
media/HLS/DASH handling (`src/Correntra.Media`) and social-site extraction
via a bundled **yt-dlp** sidecar (`src/Correntra.Agent/Runtime/YtDlpExecutor.cs`).
Browser integration is the unpacked Manifest V3 extension in
`browser-extension/`, talking to the agent over the loopback HTTP bridge
(not Chrome native messaging).

## Build / test / run

- .NET 8 SDK. `dotnet build Correntra.sln -c Debug`,
  `dotnet test Correntra.sln -c Debug --no-build`.
- **Browser integration**: the agent serves a loopback-only HTTP bridge at
  `http://127.0.0.1:27410/` from `AgentLocalHttpServer`: `GET /ping`,
  `GET /jobs`, `POST /takeover`, `POST /confirm`, `POST /media/resolve`,
  `POST /media/start`. Load `browser-extension/` unpacked in Chrome/Edge
  (`chrome://extensions` → Developer mode → Load unpacked). The extension
  needs `downloads` plus host access to that origin. Test takeover with
  `scripts/test-bridge.ps1` (or curl). **Never reintroduce Chrome native
  messaging** for this: it proved untestable from the agent side and fragile
  on this machine (manifest allowed_origins, extension IDs, silent failures).
- Full dev launch: `baslat.bat` → `scripts/dev-start.ps1` (builds solution,
  deploys `yt-dlp.exe`/ffmpeg into both bin outputs, starts agent + desktop).
- Social/video URLs are supported through the desktop app: Add URL with a
  YouTube/Facebook/… link and the agent routes the job through yt-dlp
  (`YtDlpExecutor`), merging tracks with the bundled FFmpeg sidecar.

## Environment quirks (this machine)

- `dotnet` may be absent from PATH in non-interactive shells; use
  `"C:\Program Files\dotnet\dotnet.exe"`.
- PowerShell here is v5: no `&&`; the outer toolchain strips `$_`/`$env:`
  inside `-Command` strings — write wrapper `.ps1` files (e.g. under
  `scripts/`) and run them with `-ExecutionPolicy Bypass` instead.
- Running `Correntra.Agent`/`Correntra` processes lock their bin DLLs; stop
  them before rebuilding or the copy step fails with MSB3027.
- User data lives in `%LocalAppData%\Correntra\Downloader\correntra.db`.

## Architecture rules

- Desktop ↔ Agent talk over a named pipe (length-prefixed JSON,
  `src/Correntra.Core/Ipc`, `src/Correntra.Infrastructure/Ipc`). UI state
  flows from `AgentSnapshot` polling (~1 s); speeds are derived client-side
  in `MainViewModel.MeasureSpeed` from byte deltas — do not trust averages.
- Downloads persist in SQLite; progress checkpoints are per-destination
  `*.correntra.part.checkpoint.json` files. Resume correctness depends on
  `CanResume` validators (ETag/Last-Modified) — keep them strict.
- **Transfer engine**: segmented HTTP (`src/Correntra.Transfer`) defaults to
  **8 segments** (IDM default, 1..32) with 256 KiB buffers, HTTP/2
  multiplexing and checkpoint throttling. `DownloadJobCoordinator` reads
  `desktop-settings.json:SegmentsPerDownload` so the Settings slider actually
  applies; 32 segments triggers `429` on many hosts (ash-speed: 1 seg 3.46
  MB/s vs 32 seg 1.12 MB/s).
- Social-platform and other extractor URLs (see `YtDlpExecutor`) must go
  through yt-dlp; plain HTTP fetches of those pages only save HTML. Jobs
  whose source host is a known platform, or that carry `X-Correntra-Format`,
  are routed in `DownloadJobCoordinator`; the selected quality is never sent
  to any media server.
- yt-dlp runs with `--cookies-from-browser chrome → edge → firefox` then
  falls back to an anonymous pass (Chrome locks its cookie DB while running,
  yt-dlp #7271); FFmpeg is located via `--ffmpeg-location`.
- **Browser → Agent bridge is dual-layer** after 0.3.4: (1) content-script
  click interceptor for obvious file links (`.bin/.zip/.exe/[download]`) via
  `correntra.takeoverUrl` before a `DownloadItem` exists, and (2) hardened
  `chrome.downloads.onCreated` + `onDeterminingFilename` that **pauses first**
  (IDM-style) and keeps the MV3 worker alive with `alarms`. Never reintroduce
  native messaging.

## Licensing gates

- **The project itself is FSL-1.1-MIT** (Functional Source License; converts
  to MIT two years after each version) since 2026-08-25 — see LICENSE.txt.
  Competing commercial use is prohibited for everyone including contributors.
- FFmpeg sidecar must stay LGPL-only; `scripts/get-ffmpeg.ps1` rejects
  `--enable-gpl`/`--enable-nonfree` builds. yt-dlp is Unlicense. Never add
  GPL/AGPL/SSPL or non-commercial-only dependencies. Keep
  `THIRD-PARTY-NOTICES.md` and `CHANGELOG.md` in sync with changes.
- FFmpeg is pinned to an **immutable dated autobuild tag**
  (`autobuild-2026-08-24-13-10`), NOT the rolling `latest` tag whose assets
  are replaced in place and break checksum verification. To upgrade: pick a
  new dated tag, update tag/name/sha256 together in `get-ffmpeg.ps1`.
- Every code change must ship with an updated `CHANGELOG.md` entry (under
  Unreleased / next-version heading) and a push to origin — the maintainer
  treats both as part of done.
- **Auto-push is enforced by git, not by memory**: `core.hooksPath` points at
  `scripts/hooks`, whose post-commit hook pushes to origin after EVERY commit
  (safe-fail: warns on offline/diverged instead of blocking). Never delete or
  bypass this hook. Do not run long batches of commits expecting to "push at
  the end" — each commit lands on GitHub immediately.

## Coding conventions

- C#: file-scoped namespaces, `ConfigureAwait(false)` in library code,
  XML doc comments on public members, records for wire models, defensive
  validation at IPC boundaries (`AgentCommandDispatcher` is the reference).
- Extension TS: strict, no DOM libraries beyond Chrome types; overlay UI is
  isolated in a closed ShadowRoot and must survive hostile page CSS.
- User-visible strings go through `LocalizationService` in **both** Turkish
  and English; a key missing from one dictionary falls back silently.
- The browser extension ships inside the app (`browser-extension/` next to
  Correntra.exe) and is activated via the first-run `ExtensionSetupDialog`
  wizard (Settings → Browser can reopen it). Do not remove the folder copy
  step from `scripts/release.ps1` — the wizard depends on it.
- Avalonia gotchas: incremental builds can emit "No precompiled XAML" at
  runtime — clean-rebuild before launching. `TranslateY`/`StrokeLineJoin`
  style setters are not supported on Window/Polyline.
- Prefer editing existing files; keep comments explaining *why* (failure
  modes, not intent).

## Definition of done

1. `dotnet build` clean (0 warnings) and all test projects green.
2. **CLI end-to-end download test**: with the agent running, run
   `scripts/test-bridge.ps1` — it posts a takeover over the loopback bridge,
   confirms it and polls `/jobs` until terminal. A generic `download` name
   must come back renamed (e.g. `yt-dlp.exe`) and state must reach 9
   (Completed).
3. Update `CHANGELOG.md` (and `THIRD-PARTY-NOTICES.md` for new components).
4. Restart via `dev-start.ps1` and remind the user to reload the extension.

## Skills

- Project skills live in `.opencode/skills/*/SKILL.md` (committed, portable).
  Global vibe skills live in `~/.config/opencode/skills/*/SKILL.md`.
  See `.opencode/skills/correntra-guardrails/SKILL.md` for the project checklist.
