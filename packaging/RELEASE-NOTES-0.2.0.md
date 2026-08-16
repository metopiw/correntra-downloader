# Correntra Downloader 0.2.0

## Highlights
- **Loopback bridge extension integration**: the browser extension now hands
  downloads to the app over `http://127.0.0.1:27410` (IDM-style), replacing
  native messaging entirely. Install `Correntra-Browser-Extension-0.2.0.zip`
  via `chrome://extensions` → Load unpacked.
- **Faster, more reliable transfers**: up to 32 parallel segments, HTTP/2
  multiplexing, stall timeouts, per-segment retries and automatic job-level
  re-queue with durable resume (checkpoint files).
- **Real file names**: generic names like `download` are replaced from the
  server's Content-Disposition / redirect URL / MIME type before saving.
- **Live speed & ETA** columns in the main window; double-click opens files.
- **Social video engine**: paste YouTube/Facebook/X/Instagram/… links via
  Add URL; the bundled yt-dlp sidecar extracts and merges tracks with the
  bundled LGPL FFmpeg.

## Notes
- FFmpeg sidecar remains LGPL-only; yt-dlp is Unlicense. See
  `THIRD-PARTY-NOTICES.md` and `sbom.cdx.json` in the package.
- Windows 10 1809+ (x64).
