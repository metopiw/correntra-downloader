# Correntra Downloader

**Akışı yakala. Hızla tamamla.**

Correntra Downloader is a serious Windows 10/11 download manager for regular
files and unprotected web media. It combines resumable segmented transfers,
queues and scheduling, HLS/DASH handling, and an IDM-inspired—but original—
desktop workflow. Chrome/Edge integration is the unpacked **Correntra Catch**
extension in `browser-extension/`, which talks to the agent over
`http://127.0.0.1:27410/` (not native messaging).

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

Load the extension (Chrome or Edge):

1. Start the app with `.\baslat.bat` so the agent owns port 27410.
2. Open `chrome://extensions` → Developer mode → **Load unpacked**.
3. Choose the `browser-extension` folder in this repository.
4. Reload the extension after rebuilding the agent.

The agent listens on `http://127.0.0.1:27410/` (`GET /ping`, `GET /jobs`,
`POST /takeover`, `POST /confirm`, `POST /media/resolve`, `POST /media/start`).

## Responsible use

Correntra is intended for files and media that the user owns or is authorised
to download. It does not bypass Widevine, PlayReady, FairPlay, EME, or other
DRM systems. Website terms and content rights remain the user's responsibility.

## License

The Correntra source is published under the Functional Source License
(FSL-1.1-MIT): anyone may use, study, modify, and contribute to the code, but
competing commercial uses are not permitted. Two years after each release, that
version automatically converts to the MIT License. Runtime dependencies retain
their permissive or separately documented licenses. See [`LICENSE.txt`](LICENSE.txt)
and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
