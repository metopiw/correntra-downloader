# Correntra browser extension

Manifest V3 integration shared by Chrome and Edge. It is intentionally loaded
unpacked from `dist`; it contains no remote code, analytics, advertising, or
DRM-circumvention logic.

## Build and test

Requires Node.js 22 or newer.

```powershell
npm ci
npm test
npm run build
```

The build is written to `dist`. Automated tests cover classification,
candidate deduplication/expiry/caps, opaque IDs, URL and credential redaction,
and Native Messaging envelopes. They do not claim a real Chrome/Edge or
third-party website compatibility run; those remain release-matrix tests on
Windows 10 and 11.

## Load unpacked

1. Build the extension.
2. Open `chrome://extensions` or `edge://extensions`.
3. Turn on **Developer mode**, choose **Load unpacked**, and select `dist`.
4. Open the Correntra popup and enable browser integration. Chrome/Edge asks
   for HTTP/HTTPS site access at that explicit user action.

The per-site switch immediately disables takeover and overlays for that host.
Turning the master switch off unregisters future content injection; normal
browser downloads are no longer paused or redirected.

The manifest carries only a fixed **public** key so unpacked Chrome and Edge
installations receive the stable extension ID
`fbngehclfngjenhlchnkojooliaifggj`. The Native Messaging registration allow
lists that exact origin. No private signing key is stored in this repository.

## Privacy and permissions

- `downloads` pauses a newly created download only while integration and the
  current site are enabled. The Native Host has 1.2 seconds to accept it. On
  acceptance the browser item is cancelled and erased; timeout, rejection, or
  host failure resumes the browser download.
- `webRequest` observes URL, response MIME, content length, and disposition. It
  does not use `webRequestBlocking`, capture `Cookie`/`Authorization` headers,
  or persist request headers.
- HTTP/HTTPS host access is optional and requested by the master-switch click.
- `cookies` is optional. The popup can request it for one selected media
  download; only matching cookies are sent transiently to the Native Host and
  the permission is then removed. Cookie values and signed query values never
  enter extension storage or logs.
- Candidate records are bounded in `storage.session` (20-minute TTL, 36 per
  tab, 160 total). Pages and UI receive random opaque IDs and redacted display
  metadata, never privileged candidate URLs.

Direct video/audio, HLS/DASH manifests, Googlevideo, Instagram CDN, and X video
requests receive detection hints. Site adapters are best-effort because those
services change; encrypted EME/Widevine/PlayReady media is reported rather than
bypassed.

## Native Messaging protocol

Host name: `com.correntra.downloader`. Chrome performs the UInt32 framing; the
extension and host exchange UTF-8 JSON objects capped at 256 KiB. All field
names are camelCase.

Every request has this envelope:

```json
{
  "protocolVersion": 1,
  "kind": "host.ping | takeover.offer | media.start",
  "requestId": "r_<opaque-id>",
  "timestampUtc": "2026-08-13T12:00:00.000Z",
  "payload": {}
}
```

The correlated response is:

```json
{
  "protocolVersion": 1,
  "kind": "response",
  "requestId": "r_<same-opaque-id>",
  "timestampUtc": "2026-08-13T12:00:00.100Z",
  "payload": { "accepted": true, "hostVersion": "0.1.0" }
}
```

`takeover.offer` payload fields are `browserDownloadId`, `url`, `finalUrl`,
`filename`, optional `mime`, optional `totalBytes`, optional `referrer`,
`incognito`, and an allow-listed `headers` object (User-Agent/Referer only).

`media.start` fields are `candidateId`, `url`, optional `referrer`, `media`
(`kind`, `title`, `pageHost`, `source`, and optional format/quality/size), plus
optional one-shot `authContext`. The host must treat every field as untrusted,
validate sizes/schemes, and never log credentials or unredacted signed URLs.
