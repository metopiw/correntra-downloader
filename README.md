# Correntra Downloader

**Akışı yakala. Hızla tamamla.**

Correntra Downloader is a serious Windows 10/11 download manager for regular
files and unprotected web media. It combines resumable segmented transfers,
queues and scheduling, HLS/DASH handling, and an IDM-inspired—but original—
desktop workflow. Browser integration is intentionally not bundled: the agent
exposes a loopback HTTP bridge (`http://127.0.0.1:27410/`) that any future
extension can use (see `AGENTS.md`).

The application is being developed independently. GPL/AGPL/SSPL code is not
part of the product, and `yt-dlp.exe` is not bundled. DRM circumvention is not
implemented.

## Development status

This repository is under active construction. The architecture and release
acceptance criteria are documented under [`docs/`](docs/).

## Local development

Prerequisites:

- Windows 10 or 11, x64
- .NET SDK 8.0.404 or a compatible 8.0 patch

Run from source:

```powershell
.\baslat.bat
```

Build and test:

```powershell
dotnet restore Correntra.sln
dotnet build Correntra.sln -c Release --no-restore
dotnet test Correntra.sln -c Release --no-build
```

No browser extension ships with the product. The agent listens on
`http://127.0.0.1:27410/` (`GET /ping`, `GET /jobs`, `POST /takeover`,
`POST /confirm`) so a separately developed extension can hand downloads to
the app without native messaging.

## Responsible use

Correntra is intended for files and media that the user owns or is authorised
to download. It does not bypass Widevine, PlayReady, FairPlay, EME, or other
DRM systems. Website terms and content rights remain the user's responsibility.

## License

The Correntra source is source-available and all rights are reserved. Runtime
dependencies retain their permissive or separately documented licenses. See
[`LICENSE.txt`](LICENSE.txt) and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
