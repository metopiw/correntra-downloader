# Correntra Downloader 0.2.1

## Highlights
- **Standalone app**: bundled browser extensions removed. The agent keeps a
  loopback HTTP bridge (`http://127.0.0.1:27410/`) so a separately developed
  extension can still hand downloads to the app — no native messaging.
- Everything from 0.2.0: segmented high-speed transfers, durable resume,
  live speed/ETA, real file names, yt-dlp social video support via Add URL.

## Notes
- Windows 10 1809+ (x64). FFmpeg sidecar LGPL-only; yt-dlp Unlicense.
- See `THIRD-PARTY-NOTICES.md` and `sbom.cdx.json` in the package.
