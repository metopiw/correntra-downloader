# AGENTS.md — guidance for coding agents in this repository

Correntra is an IDM-class download manager: Avalonia desktop shell
(`src/Correntra.Desktop`), a background transfer agent
(`src/Correntra.Agent`), a Chrome/Edge capture extension
(`browser-extension/`), a native messaging host (`src/Correntra.NativeHost`),
a segmented HTTP engine (`src/Correntra.Transfer`), media/HLS/DASH handling
(`src/Correntra.Media`) and social-site extraction via a bundled **yt-dlp**
sidecar (`src/Correntra.Agent/Runtime/YtDlpExecutor.cs`).

## Build / test / run

- .NET 8 SDK. `dotnet build Correntra.sln -c Debug`,
  `dotnet test Correntra.sln -c Debug --no-build`.
- Legacy v1 extension (`browser-extension/`, TypeScript + esbuild) is kept for
  rollback only (`browser-extension-backup/`); it needs `npm run build`.
- **v2 extension (the one to ship)**: `browser-extension-v2/` is build-free
  plain JS (manifest + background.js + popup.*). Chrome loads the folder
  directly; there is nothing to compile. It only performs browser-download
  takeover; video capture was removed by user decision.
- **Extension ↔ agent transport is the loopback HTTP bridge**, not native
  messaging: the agent serves `http://127.0.0.1:27410/` (`/takeover`,
  `/confirm`, `/jobs`, `/ping`) from `AgentLocalHttpServer`. Test it with
  `scripts/test-bridge.ps1` (or curl). Never reintroduce native messaging for
  extension flows; it proved untestable and fragile on this machine.
- Full dev launch: `baslat.bat` → `scripts/dev-start.ps1` (builds solution,
  builds the legacy extension when npm exists, deploys `yt-dlp.exe`/ffmpeg
  into both bin outputs, starts agent + desktop).
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
- Social-platform URLs (see `YtDlpExecutor.SupportedDomains`) must go through
  yt-dlp; plain HTTP fetches of those pages only save HTML. Jobs whose source
  host is social are routed automatically in `DownloadJobCoordinator`; the
  selected quality is carried in the job header `X-Correntra-Format` (never
  sent to any server).
- yt-dlp runs with `--cookies-from-browser chrome` first and falls back to an
  anonymous pass; FFmpeg is located via `--ffmpeg-location`.

## Licensing gates

- FFmpeg sidecar must stay LGPL-only; `scripts/get-ffmpeg.ps1` rejects
  `--enable-gpl`/`--enable-nonfree` builds. yt-dlp is Unlicense. Never add
  GPL/AGPL/SSPL or non-commercial-only dependencies. Keep
  `THIRD-PARTY-NOTICES.md` and `CHANGELOG.md` in sync with changes.

## Coding conventions

- C#: file-scoped namespaces, `ConfigureAwait(false)` in library code,
  XML doc comments on public members, records for wire models, defensive
  validation at IPC boundaries (`AgentCommandDispatcher` is the reference).
- Extension TS: strict, no DOM libraries beyond Chrome types; overlay UI is
  isolated in a closed ShadowRoot and must survive hostile page CSS.
- Prefer editing existing files; keep comments explaining *why* (failure
  modes, not intent).

## Definition of done

1. `dotnet build` clean (0 warnings) and all test projects green.
2. v2 extension: `node --check` on its JS files (no build step).
3. **CLI end-to-end download test**: with the agent running, run
   `scripts/test-bridge.ps1` — it posts a takeover over the loopback bridge,
   confirms it and polls `/jobs` until terminal. A generic `download` name
   must come back renamed (e.g. `yt-dlp.exe`) and state must reach 9
   (Completed).
4. Update `CHANGELOG.md` (and `THIRD-PARTY-NOTICES.md` for new components).
5. Restart via `dev-start.ps1` and remind the user to reload the extension.
