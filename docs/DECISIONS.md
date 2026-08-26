# Architecture Decision Records

Short, chronological log of **why** we chose X over Y. A new agent reading
this should never re-litigate a settled decision. Newest entries at the top.
One paragraph per decision: context → choice → consequence.

---

## 2026-08-26 — Extension identity is pinned (fixed manifest key + exact Origin match)

**Context:** The bridge accepted any `chrome-extension://` Origin, so a
malicious browser extension could invoke it. A fixed ID was impossible before
because unpacked installs derive the ID from the folder path.

**Decision:** Embed a fixed RSA `key` in `browser-extension/manifest.json`;
Chrome now derives the canonical ID `bhnibkknmmodoehpaeoijnkabfdmbdjp` on
every machine (`Correntra.Core.BrowserExtensionIdentity`). Both the loopback
HTTP bridge and the native validator match that exact Origin — no prefixes.
The old path-derived ID `fbngehc…` is retired; users must reload the
extension once after updating.

**Consequence:** Any other extension or web page receives 403. The remaining
accepted gap: local non-browser processes can still call without an Origin
(by design, for curl/tests) — a shared-secret handshake was considered and
deferred. Key regeneration requires touching 4 files together (see AGENTS.md).

---

## 2026-08-25 — Browser extension ships inside the app + first-run wizard

**Context:** The installer never contained the extension; users had to find
`browser-extension/` manually and "Load unpacked" — most could not.

**Decision:** `release.ps1` copies `browser-extension/` next to Correntra.exe
and a first-run `ExtensionSetupDialog` wizard guides the 4-click setup (opens
folder, copies path to clipboard, launches `chrome://extensions`). Shown only
when capture is not connected and `DesktopSettings.ExtensionSetupShown` is
false. Reopenable from Settings → Browser.

**Consequence:** Do not remove the folder copy from release.ps1; do not gate
the wizard behind a store install. Web-store distribution ($5 dev fee) remains
a future option for zero-click installs via registry key.

## 2026-08-25 — Project license is FSL-1.1-MIT, not MIT

**Context:** Original custom license ("all rights reserved") scared off
contributors while still allowing code theft; user wants others to build on
the project but not resell it. User has no budget for store fees.

**Decision:** Adopt Functional Source License v1.1 (FSL-1.1-MIT): anyone may
use/study/modify/contribute, competing commercial use prohibited, each version
auto-converts to MIT two years after release.

**Consequence:** CONTRIBUTING.md states the ground rules. Never relabel the
project "open source" (it is *source-available* until each 2-year conversion).

## 2026-08-25 — FFmpeg pinned to dated autobuild tag

**Context:** CI release broke because `BtbN/FFmpeg-Builds` "latest" tag
replaces assets in place, silently invalidating the pinned SHA-256.

**Decision:** Pin to immutable dated tag `autobuild-2026-08-24-13-10`
(n8.1.2-44-g7c533d0f86). Upgrade = update tag + archive name + folder name +
SHA-256 together in `scripts/get-ffmpeg.ps1`.

## 2026-08-25 — Visual language pass over the desktop shell

**Context:** UI worked but felt assembled rather than designed; font sizes
were ad-hoc (10/10.5/12/13/14/16/17/19/21/28).

**Decision:** Fixed type scale 11 (meta) / 13 (body) / 15 (section) / 21
(page/dialog heading); 120 ms background transitions on buttons and grid rows;
180 ms fade-in on dialog windows. Avalonia does not support TranslateY or
StrokeLineJoin setters on Window/Polyline — verified by failed build.

**Consequence:** New UI must reuse these four sizes, not invent new ones.
Accent teal marks interactive elements; Success/Warning/Danger stay semantic.

## 2026-08-25 — Optional VirusTotal scan is hash-only and opt-in

**Context:** Users cannot tell whether a finished download is safe; full-file
upload would be a privacy regression.

**Decision:** Settings → Privacy accepts a user-supplied VirusTotal API key.
On completion the app sends ONLY the SHA-256 digest to the v3 files endpoint;
the file itself never leaves the machine. Verdict renders under the file name,
red when detections > 0. Rate limit (4 req/min public tier) handled by a
per-hash session cache.

## 2026-08-25 — Playlists expand into per-entry numbered files

**Context:** yt-dlp defaults to single-item mode here (`--no-playlist`);
users reported a 60-song playlist arriving as one merged 160 MB file.

**Decision:** `DownloadJobCoordinator.LooksLikePlaylist()` detects list=/
playlist URLs and switches yt-dlp to playlist mode with output template
`%(playlist_index)03d - <name>%(ext)s` inside a subfolder named after the job.
Completion counts the produced folder instead of hunting one file.

**Consequence:** Merge/remux still applies to video+audio of EACH entry, not
across entries. Progress stays approximate for playlists (byte totals are
per-track).

## 2026-08-18 — Loopback HTTP bridge instead of Chrome native messaging

**Context:** Native messaging proved untestable from the agent side and
fragile on this machine (manifest allowed_origins, extension IDs, silent
failures).

**Decision:** Extension talks HTTP to `http://127.0.0.1:27410/`, origin-
locked to the extension. Dual-layer capture: content-script click interceptor
+ hardened `chrome.downloads.onCreated/onDeterminingFilename` that pauses the
browser download first (IDM-style).

**Consequence:** NEVER reintroduce native messaging. Bridge endpoints:
/ping /jobs /takeover /confirm /media/resolve /media/start.

## 2026-08-15 — Segmented transfer engine defaults to 8 segments

**Context:** IDM parity; measured throughput peaks around 8 segments, 32
triggers 429 throttling on many hosts (ash-speed test: 1 seg 3.46 MB/s vs
32 seg 1.12 MB/s).

**Decision:** Default 8, range 1..32, 256 KiB buffers, HTTP/2 multiplexing,
checkpoint throttling. `SegmentsPerDownload` read live from
desktop-settings.json.

## 2026-08-15 — Localization via hand-rolled dictionary service, not .resx

**Context:** Only tr/en supported; satellite assemblies add packaging
complexity for no gain at this scale.

**Decision:** `LocalizationService` holds both dictionaries in code; views
bind `{DynamicResource key}`; switching language re-applies all resources at
runtime. Startup reads `DesktopSettings.Language` (NOT system culture).

**Consequence:** Every new string must be added to BOTH dictionaries — an
English-only key silently falls back for Turkish users.
