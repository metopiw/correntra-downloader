# FFmpeg sidecar notice

Correntra invokes `ffmpeg.exe` as a replaceable, separate process for
unencrypted media remuxing and audio conversion. Correntra does not link to
FFmpeg libraries and does not contain FFmpeg source code.

This package uses BtbN's FFmpeg 8.1 Windows x64 `lgpl-shared` build, pinned
to an immutable dated autobuild tag. The release gate verifies the upstream
archive SHA-256 and rejects a build whose reported configuration contains
`--enable-gpl` or `--enable-nonfree`.

- Archive: `ffmpeg-n8.1.2-52-g5a03dfa0f6-win64-lgpl-shared-8.1.zip`
- Pinned upstream tag: `autobuild-2026-09-11-13-20`
- SHA-256: `1EA9DEDBA28E39067BC1738935FECD376F5FA1E1A77F15F3A4488C176F12ED9B`
- Upstream build scripts: <https://github.com/BtbN/FFmpeg-Builds>
- FFmpeg source: <https://github.com/FFmpeg/FFmpeg/tree/n8.1>
- Upstream binary feed:
  <https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-11-13-20>

These values must stay identical to `scripts/get-ffmpeg.ps1`, which is the
single source of truth; `scripts/check-docs.ps1` enforces the match in CI.

The exact LGPLv3 license supplied by the binary distributor is included as
`LICENSE.txt` in this directory. You may replace the contents of this folder
with another compatible FFmpeg build; Correntra verifies the build before use.

