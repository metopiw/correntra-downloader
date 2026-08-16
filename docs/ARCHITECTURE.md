# Architecture

The current compatibility baseline is .NET 8 + Avalonia 11.3 LTS-quality patch line. This deliberately
supports the user's Windows 10 LTSC build, where the installed .NET 10 compiler
cannot execute because of CET requirements. The code avoids framework-specific
coupling so the baseline can move to .NET 10 once the Windows 10 support matrix
and build hosts are upgraded.

## Shape

```text
Chrome / Edge MV3 extension
        │ Native Messaging (framed JSON)
        ▼
Correntra.NativeHost ── user-restricted named pipe ── Correntra.Agent
                                                        │
                           ┌────────────────────────────┼─────────────────────┐
                           ▼                            ▼                     ▼
                    transfer engine             media resolver        SQLite/WAL
                           │                            │
                           └──────── partial files ─────┴── LGPL FFmpeg sidecar
                                                        ▲
                                                        │ named pipe
                                                Correntra.Desktop
```

The Agent is the sole owner of jobs, database writes, partial files, queue
state, and transfer workers. Closing or restarting the Desktop must not stop an
active transfer. NativeHost contains no download logic and exposes no TCP port.

## Projects

- `Correntra.Core`: immutable domain models, state machine, validation,
  categorisation, scheduling contracts, IPC envelopes.
- `Correntra.Transfer`: HTTP probing, segmented/single-stream transfer,
  checkpoints, retries, bandwidth limiting, hashing, safe finalisation.
- `Correntra.Media`: candidates, clear HLS/DASH parsing and planning, site
  adapters, FFmpeg command planning and DRM classification.
- `Correntra.Infrastructure`: SQLite repositories, migrations, settings,
  filesystem layout, named-pipe protocol, structured redacted logging.
- `Correntra.Platform.Windows`: HKCU Native Messaging registration, DPAPI,
  startup, Mark-of-the-Web, shell actions, notifications, and power actions.
- `Correntra.Agent`: long-lived process composition, queue coordinator,
  scheduler, transfer lifecycle, and IPC server.
- `Correntra.NativeHost`: Chrome/Edge stdio framing and strict relay.
- `Correntra.Desktop`: Avalonia MVVM interface, confirmation dialogs, settings,
  queue and history views, audio preview.
- `browser-extension`: Manifest V3 service worker, content/MAIN-world scripts,
  overlay, popup, options, detection, and Native Messaging client.

## Invariants

- Job state transitions are validated in Core. UI state never substitutes for
  persisted Agent state.
- Final filenames never point to partially written content. Work happens in a
  same-volume temporary file and completes by flush plus atomic rename.
- Each resumable HTTP checkpoint is bound to URL, length, ETag/Last-Modified,
  and completed byte ranges. Validator mismatch invalidates unsafe ranges.
- Credentials are scoped to one origin/job, encrypted at rest only when resume
  requires it, redacted everywhere, and deleted on completion/expiry.
- Extension page messages carry opaque candidate IDs, not arbitrary privileged
  URLs. MAIN-world data requires validation/correlation.
- Remote updates contain no dynamically executed extension/site-adapter code.
  Compatibility fixes ship as signed application releases.

## Transfer state machine

```text
Pending -> Probing -> Queued -> Downloading -> Verifying -> Finalizing -> Completed
               \         \          │              │
                \         -> Paused <-              └-> Failed
                 └-> NeedsInput        Downloading -> Cancelling -> Cancelled
```

Interrupted active states recover to `Queued` or `Paused` according to the
user's prior intent. Terminal states never transition back without an explicit
retry/restore operation that creates a new attempt.

## IPC

Agent pipe: `Correntra.Agent.{user-sid-hash}.v1` with current-user-only ACL on
Windows. Frames are little-endian UInt32 length followed by UTF-8 JSON. Each
envelope includes protocol version, message kind, request ID, timestamp, and a
typed payload. Messages are capped at 256 KiB. Unknown types/fields are ignored
only when forward-compatible; invalid lengths and CR/LF header injection are
rejected.

Desktop uses commands plus snapshot polling/event subscription. NativeHost
validates the browser caller origin, strips unrecognised fields, and relays only
allow-listed commands.
