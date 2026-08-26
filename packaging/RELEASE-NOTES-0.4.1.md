# Correntra Downloader 0.4.1

Security-hardening release for the browser integration.

## Highlights

- **Only the genuine Correntra extension can talk to the app.** The browser
  extension's identity is now pinned (fixed key in its manifest), and the
  local bridge accepts that exact origin only — other extensions and web
  pages are rejected.
- **Per-run shared secret on the local bridge.** Correntra provisions an
  unguessable token into its own extension folder at every start; command
  requests must carry it. Other local programs can no longer imitate the
  extension to trigger downloads. Everyday usage is unchanged.
- **Smaller attack surface:** the unused native-messaging host component was
  removed from the codebase entirely.
- **Stronger CI:** formatting check (`dotnet format`) and the third-party
  license gate now run on every push, not only at release time.
- FFmpeg pin documentation now matches the actual pinned build and is
  enforced by the documentation consistency gate in CI.

## After installing

If you already use the browser extension, open `chrome://extensions` (or
`edge://extensions`), find **Correntra Catch** and press its reload button
once. That re-reads the pinned identity; everything else works as before.

The extension folder shipped with this release contains a per-run secret
(`bridge-token.txt`) that changes whenever Correntra runs. Keep using the
"Load unpacked" flow from Settings → Browser if you reinstall it.
