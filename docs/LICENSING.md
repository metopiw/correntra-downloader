# Dependency and licensing policy

The product must preserve the option to become closed source. New runtime or
build dependencies require an explicit license review.

## Allow list

- MIT
- BSD-2-Clause / BSD-3-Clause
- Apache-2.0
- ISC
- Unlicense
- Public domain
- SIL OFL for fonts
- LGPL only for a replaceable, separately executed FFmpeg distribution with
  full compliance material

## Deny list

- GPL, AGPL, SSPL
- non-commercial, research-only, field-of-use, or source-available dependencies
  that restrict commercial distribution
- binaries of unknown provenance or build configuration

`yt-dlp.exe` is specifically denied: its official PyInstaller Windows binary is
GPLv3+ as a combined work. yt-dlp source may be consulted as public behavioural
reference, but no extractor implementation is copied and it is not a runtime
dependency.

Avalonia Accelerate is not used. Inno Setup is not used. Velopack (MIT) handles
packaging and GitHub Releases updates.

CI restores only lock-file-pinned dependencies, produces a CycloneDX/SPDX SBOM,
and fails on denied or unknown licenses. Every release includes exact notices,
license texts, and FFmpeg build/source compliance material when FFmpeg is
bundled.

