# Correntra Downloader 0.4.2

Setup-experience fix release.

## Highlights

- **Fixed: the extension setup wizard never appeared on fresh installs.**
  A "shown once" flag survived reinstalls and the wrong signal gated it, so
  most users never saw the guided setup. The wizard now appears whenever
  the genuine browser extension has not connected (~10 seconds after
  launch) — every launch, until it does.
- **Fixed: the "extension connected" status indicator was misleading.** It
  now reflects real, verified contact from the browser extension (pinned
  extension identity + bridge token), not merely the app's own backend.
- The extension now sends a lightweight authenticated heartbeat every
  minute, keeping the status accurate even while idle. The heartbeat also
  picks up a regenerated bridge token after an app restart.
- After updating, reload the extension once in `chrome://extensions` (or
  `edge://extensions`) so it picks up the new heartbeat behaviour.
