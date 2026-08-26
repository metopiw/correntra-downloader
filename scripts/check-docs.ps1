# Documentation consistency gate.
# Catches the two failure modes that poison future agents:
#   1) docs referencing files/dirs that no longer exist
#   2) CHANGELOG lagging behind the assembly version
# Run by CI on every push; agents should run it before finishing a task:
#   powershell -File scripts/check-docs.ps1

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $repoRoot

$failures = New-Object System.Collections.Generic.List[string]

# --- 1) Every repo path mentioned in tracked docs must exist -----------------
$docs = @(Get-ChildItem -File | Where-Object { $_.Extension -eq ".md" }) +
        @(Get-ChildItem docs -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq ".md" })

$pathPattern = '(?<![:/\w])((?:src|scripts|tests|docs|packaging|browser-extension|artifacts)/[A-Za-z0-9_\-./]+)'

foreach ($doc in $docs) {
    $lines = Get-Content -LiteralPath $doc.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # Strip inline code spans first so `X/Y` fragments inside backticks are
        # checked, then find repo-rooted paths.
        $matchesFound = [regex]::Matches($lines[$i], $pathPattern)
        foreach ($m in $matchesFound) {
            $candidate = $m.Groups[1].Value.TrimEnd('.', ',', ')', ']', '`')
            # Skip wildcard-ish or obviously templated mentions.
            if ($candidate -match '\*' -or $candidate -match '<' ) { continue }
            $full = Join-Path $repoRoot $candidate
            if (-not (Test-Path -LiteralPath $full)) {
                # Gitignored runtime artifacts (generated at app start) are
                # legitimately referenced by docs although absent from a
                # fresh checkout.
                git check-ignore -q -- "$candidate" 2>$null
                if ($LASTEXITCODE -eq 0) { continue }
                $failures.Add("$($doc.Name):$($i + 1) references missing path '$candidate'")
            }
        }
    }
}

# --- 2) CHANGELOG must know the current version ------------------------------
$props = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
$version = [regex]::Match($props, '<VersionPrefix>([^<]+)</VersionPrefix>').Groups[1].Value
if (-not $version) { $failures.Add("Directory.Build.props: cannot read VersionPrefix") }

$changelog = Get-Content -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") -Raw
if ($changelog -notmatch [regex]::Escape("## $version")) {
    $failures.Add("CHANGELOG.md has no '## $version' heading (VersionPrefix moved on without a release entry?)")
}

# --- 3) Key anchors that keep agents oriented --------------------------------
$anchors = @(
    @{ File = "AGENTS.md";        Pattern = "FSL-1\.1-MIT" },
    @{ File = "AGENTS.md";        Pattern = "(?i)auto-push" },
    @{ File = "AGENTS.md";        Pattern = "DECISIONS\.md" },
    @{ File = "docs/DECISIONS.md"; Pattern = "^## \d{4}-\d{2}-\d{2}" },
    @{ File = "CONTRIBUTING.md";  Pattern = "Functional Source License" }
)
foreach ($anchor in $anchors) {
    $file = Join-Path $repoRoot $anchor.File
    if (-not (Test-Path -LiteralPath $file)) {
        $failures.Add("missing required file '$($anchor.File)'")
        continue
    }
    $content = Get-Content -LiteralPath $file -Raw
    if ([regex]::Matches($content, $anchor.Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline).Count -eq 0) {
        $failures.Add("$($anchor.File): expected anchor /$($anchor.Pattern)/ is gone - docs drifted")
    }
}

# --- 3b) FFmpeg pin values: notice must match the script ---------------------
$ffmpegScript = Join-Path $repoRoot "scripts/get-ffmpeg.ps1"
$ffmpegNotice = Join-Path $repoRoot "packaging/FFMPEG-NOTICE.md"
foreach ($pair in @(
    @{ File = $ffmpegScript; Field = "releaseTag";    Pattern = '\$releaseTag\s*=\s*"([^"]+)"' },
    @{ File = $ffmpegScript; Field = "archiveName";   Pattern = '\$archiveName\s*=\s*"([^"]+)"' },
    @{ File = $ffmpegScript; Field = "expectedSha256"; Pattern = '\$expectedSha256\s*=\s*"([^"]+)"' }
)) {
    if (-not (Test-Path -LiteralPath $pair.File)) {
        $failures.Add("missing required file 'scripts/get-ffmpeg.ps1'")
        continue
    }
    $content = Get-Content -LiteralPath $pair.File -Raw
    $value = [regex]::Match($content, $pair.Pattern).Groups[1].Value
    if (-not $value) {
        $failures.Add("get-ffmpeg.ps1: cannot read $($pair.Field)")
        continue
    }

    $noticeContent = Get-Content -LiteralPath $ffmpegNotice -Raw
    if ($noticeContent -notmatch [regex]::Escape($value)) {
        $failures.Add("FFMPEG-NOTICE.md does not mention get-ffmpeg.ps1's $($pair.Field) '$value' - pin drift")
    }
}

# --- Report ------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host "DOC CHECK FAILED - fix these before finishing:"
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}

Write-Host "Doc check passed: referenced paths exist, CHANGELOG covers v$version, anchors intact."
exit 0
