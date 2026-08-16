# Product specification

## Product promise

Correntra Downloader is a dependable daily-use Windows 10/11 download manager,
not a disposable MVP. A user can hand regular browser downloads to Correntra,
capture accessible unprotected video/audio, choose quality and destination,
schedule or queue work, and recover cleanly from app, network, or machine
interruptions.

The initial UI is Turkish and English. No account or cloud service is required.
Settings and history remain on the device. Update metadata and release assets
come from GitHub Releases.

## Committed user experience

- Chrome and Edge share one externally loaded Manifest V3 extension.
- A prominent extension switch immediately enables/disables all takeover.
- A normal browser download is paused, offered to Correntra, and resumed in the
  browser if Correntra does not acknowledge it quickly.
- Correntra opens an IDM-style confirmation window with name, category,
  destination, size when known, **Download now**, **Download later**, and
  **Cancel**.
- Pages with detected media show an isolated **Download video** or
  **Download music** overlay plus the extension popup's candidate list.
- Media selection shows quality, codec/container, approximate size, audio and
  subtitle options. Separate audio/video tracks are merged automatically.
- YouTube, Instagram, and X receive dedicated adapters plus generic network
  detection. They require ongoing compatibility maintenance; no universal
  permanence claim is made.
- DRM is detected and reported, never bypassed.
- Categories default to Compressed, Documents, Music, Programs, and Video.
  Users may add categories and extension/site routing rules.
- Queues support start/stop time, selected days, concurrency, speed limits, and
  completion actions with a cancellable countdown.
- Bulk tools cover pasted URL lists, numeric URL generation, selected page
  links, playlists, and selected channel/profile items when technically
  accessible.
- The site collector finds and filters assets. Full offline website mirroring
  is deliberately not a v1 requirement.
- System tray, startup option, clipboard detection, completion notification,
  audio preview, and dark/light themes are included.
- Installed and portable distributions are produced. Installed builds update
  from tagged GitHub Releases after explicit user confirmation; portable builds
  notify and link to the release.

## Supported transfer families

1. HTTP/HTTPS GET downloads, redirects, cookies/referer, Range resume, unknown
   lengths, chunked responses, filename/content-type discovery.
2. Direct audio/video files.
3. Clear HLS: master/media playlists, fMP4/TS, alternate audio/subtitles,
   byte-ranges, discontinuities, VOD/event/live, identity AES-128.
4. Clear MPEG-DASH: BaseURL, SegmentTemplate/Timeline/List, multiple periods,
   separate audio/video, subtitles, and live windows.
5. Browser-observed signed/CDN URLs and versioned site adapters.

FTP is outside the first release. POST downloads fall back to the browser when
the request cannot be replayed safely.

## Non-goals and hard boundaries

- No DRM key acquisition or circumvention.
- No credential theft, browser password reading, or blanket cookie logging.
- No remote executable code in the extension.
- No background telemetry, advertising, or tracking.
- No guarantee that a third-party site can never change incompatibly.
- No GPL/AGPL/SSPL code in the application or release bundle.

## Release acceptance criteria

The first production release is accepted only when:

1. A 10 GiB Range-capable test file can stop, process-kill, restart, resume, and
   verify byte-for-byte without redownloading completed ranges.
2. A non-Range server safely falls back to one stream and resumes only when the
   server validator permits it.
3. Extension OFF never pauses/cancels a browser download; ON with a missing host
   always lets the browser continue without file loss.
4. Direct media, HLS master/VOD/live, and DASH separate A/V fixtures complete
   and produce playable output; DRM fixtures are rejected clearly.
5. Credentials and signed query values do not appear in logs, extension storage,
   crash text, or diagnostic export.
6. Database and partial-file recovery survives Agent and Desktop termination.
7. Turkish/English, keyboard use, 100–200% DPI, dark/light themes, screen-reader
   names, and minimum contrast are verified.
8. Unit/integration/browser tests pass on Windows 10 and Windows 11 CI/manual
   matrices; Chrome and Edge behaviours match.
9. Setup, uninstall, portable ZIP, Native Messaging registration, `baslat.bat`,
   GitHub update check, SBOM, notices, and clean-machine smoke tests pass.
10. The release dependency scan contains no prohibited license or unknown binary.

