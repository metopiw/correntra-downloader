# FFmpeg sidecar notice

Correntra invokes `ffmpeg.exe` as a replaceable, separate process for
unencrypted media remuxing and audio conversion. Correntra does not link to
FFmpeg libraries and does not contain FFmpeg source code.

This package uses BtbN's FFmpeg 8.1 Windows x64 `lgpl-shared` build, pinned
to an immutable dated autobuild tag. The release gate verifies the upstream
archive SHA-256 and rejects a build whose reported configuration contains
`--enable-gpl` or `--enable-nonfree`.

- Archive: `ffmpeg-n8.1.2-44-g7c533d0f86-win64-lgpl-shared-8.1.zip`
- Pinned upstream tag: `autobuild-2026-08-24-13-10`
- SHA-256: `60AA2BE28B1BB7B95C397DDD4EEA4EF464193D2EACAF0B865B40CC976CCB4DB0`
- Upstream build scripts: <https://github.com/BtbN/FFmpeg-Builds>
- FFmpeg source: <https://github.com/FFmpeg/FFmpeg/tree/n8.1>
- Upstream binary feed:
  <https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-08-24-13-10>

These values must stay identical to `scripts/get-ffmpeg.ps1`, which is the
single source of truth; `scripts/check-docs.ps1` enforces the match in CI.

The exact LGPLv3 license supplied by the binary distributor is included as
`LICENSE.txt` in this directory. You may replace the contents of this folder
with another compatible FFmpeg build; Correntra verifies the build before use.

