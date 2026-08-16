# FFmpeg sidecar notice

Correntra invokes `ffmpeg.exe` as a replaceable, separate process for
unencrypted media remuxing and audio conversion. Correntra does not link to
FFmpeg libraries and does not contain FFmpeg source code.

This package uses BtbN's FFmpeg 8.1 Windows x64 `lgpl-shared` build. The
release gate verifies the upstream archive SHA-256 and rejects a build whose
reported configuration contains `--enable-gpl` or `--enable-nonfree`.

- Archive: `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`
- SHA-256: `C0692B85D56F2995656406425C095700117DFD7A84F8CA5AF75EBF92ED08B8A9`
- Upstream build scripts: <https://github.com/BtbN/FFmpeg-Builds>
- FFmpeg source: <https://github.com/FFmpeg/FFmpeg/tree/n8.1>
- Upstream binary feed: <https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest>

The exact LGPLv3 license supplied by the binary distributor is included as
`LICENSE.txt` in this directory. You may replace the contents of this folder
with another compatible FFmpeg build; Correntra verifies the build before use.

