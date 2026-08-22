# Security and privacy model

## Defaults

- No account, ads, tracking, usage telemetry, or background crash upload.
- Update checks retrieve public GitHub release metadata only.
- Crash reports are previewed and sent only after an explicit user action.
- Logs are local, size-rotated, short-lived, and redact credentials/tokens.

## Browser credentials

The extension does not store cookies. Logged-in YouTube/Facebook sessions
are applied by the agent via yt-dlp `--cookies-from-browser chrome`, then an
anonymous retry. Extension persistent storage must not contain credential
values.

If restart-safe persistence is needed, the Agent encrypts the credential blob
for the current Windows user and gives it a short expiry. It is removed when the
job completes, is cancelled, or expires. Site-level session sharing can be
disabled.

## Threat boundaries

- Web pages and MAIN-world scripts are hostile input.
- Native Messaging input is untrusted even with `allowed_origins`.
- URLs, filenames, response headers, manifests, playlists, subtitles, and media
  metadata are untrusted and size-limited.
- Output paths are canonicalised under an approved destination; reserved device
  names, traversal, alternate streams, and control characters are rejected.
- The Agent loopback HTTP bridge (`127.0.0.1:27410`) accepts only missing
  Origin (local tools) or `chrome-extension://` Origin. Web pages receive 403.
- Named pipes to the Desktop remain restricted to the current user.
- Downloaded files receive Windows Mark-of-the-Web where applicable and are not
  executed automatically.

## DRM

EME usage, Widevine/PlayReady/FairPlay identifiers, DASH `ContentProtection`,
PSSH, SAMPLE-AES, and non-identity HLS key formats classify media as protected.
Correntra reports the limitation and creates no decryption/key-acquisition job.

## Reporting

Security reports should avoid public disclosure until a fix is available. A
dedicated contact will be added before public release; until then use a private
GitHub security advisory on the repository.

