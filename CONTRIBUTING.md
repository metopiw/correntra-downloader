# Contributing to Correntra Downloader

First off, thank you — the project is published under the
[Functional Source License (FSL-1.1-MIT)](LICENSE.txt) precisely so people can
study it, learn from it, and send improvements back. Two years after each
release, that version converts to the MIT license automatically.

## Ground rules

- **Competing use is not permitted.** You may not sell Correntra or ship it as
  part of a competing download manager. Everything else (personal, educational,
  research, internal business use) is welcome.
- Accepted contributions are incorporated under the project's copyright. By
  opening a pull request you agree to that licensing.
- All runtime/build dependencies must pass the license review in
  [`docs/LICENSING.md`](docs/LICENSING.md) (permissive allow-list; GPL/AGPL are
  denied).

## Getting started

```powershell
git clone https://github.com/metopiw/correntra-downloader.git
cd correntra-downloader
./baslat.bat          # or: scripts/dev-start.ps1 — starts agent + desktop
dotnet test           # full test suite must pass
```

The desktop shell is Avalonia (`src/Correntra.Desktop`); the background
service is `src/Correntra.Agent`; media extraction lives behind the yt-dlp
sidecar (`src/Correntra.Media`, `src/Correntra.Agent/Runtime/YtDlpExecutor.cs`).

## Pull requests

1. One topic per PR. Keep the diff focused.
2. Add or update tests for behaviour changes (`tests/…`). CI runs the whole
   suite plus a license/SBOM check.
3. User-visible strings go through `LocalizationService` **in both Turkish and
   English** — a PR that adds a key to only one dictionary will be bounced.
4. Update `CHANGELOG.md` under an "Unreleased" heading when the change matters
   to users.

## Good first issues

Look for issues labelled **`good first issue`**: they are scoped small, touch
one project, and list acceptance criteria. Documentation fixes, localization
gaps, and small UI polish tasks are always open.

## Reporting bugs

Open a GitHub issue with: Windows version, app version (Settings → Updates →
installed version), steps to reproduce, expected vs actual behaviour. For
download failures, paste the last ~10 lines shown in the job's failure reason —
they are already URL-redacted.

## Security

Found something exploitable? Please do **not** open a public issue. Use GitHub
"Report a vulnerability" (Security tab) so fixes can land before disclosure.
