# Third-party notices

This file is the human-readable inventory. Release builds also generate an
SBOM and copy exact license texts.

| Component | Purpose | License | Bundled/linked |
| --- | --- | --- | --- |
| .NET Runtime | Runtime and base libraries | MIT | Self-contained release |
| Avalonia | Desktop UI framework | MIT | Linked NuGet dependency |
| CommunityToolkit.Mvvm | MVVM helpers | MIT | Linked NuGet dependency |
| Microsoft.Data.Sqlite | SQLite provider | MIT | Linked NuGet dependency |
| SQLite | Embedded database engine | Public domain | Native runtime dependency |
| Velopack | Installer and updater | MIT | Packaging/runtime dependency |
| Inter | User-interface typeface | SIL OFL 1.1 | Bundled through Avalonia.Fonts.Inter |
| ANGLE Windows natives | Avalonia graphics compatibility layer | BSD-3-Clause | Transitive native dependency |
| SkiaSharp / HarfBuzzSharp | Rendering and text shaping | MIT | Transitive native dependency |
| SQLitePCLRaw | SQLite managed/native provider | Apache-2.0 | Transitive runtime dependency |
| FFmpeg 8.1 shared build | Clear-media remux/audio conversion sidecar | LGPLv3+ | Separate executable and DLL set |
| yt-dlp | Social-platform video extractor sidecar | Unlicense | Separate executable, downloaded on demand |

An LGPL-only FFmpeg sidecar may be distributed for remuxing and conversion.
When present, its exact build configuration, LGPL license, corresponding-source
offer/link, and source archive are shipped beside the release. Any FFmpeg build
containing `--enable-gpl` or `--enable-nonfree` is rejected by the release gate.

The yt-dlp sidecar is Unlicensed (public-domain equivalent), so bundling it
imposes no copyleft obligations; `scripts/get-yt-dlp.ps1` fetches an official
release binary and verifies it runs. Social-site downloads are disabled when
the binary is absent.

Explicitly not distributed: GPL/AGPL/SSPL components,
Avalonia Accelerate, and non-commercial-only packages.
