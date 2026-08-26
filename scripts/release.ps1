[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.1.0",
    [switch]$SkipTests,
    [switch]$SkipFfmpegDownload
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseRoot = Join-Path $repositoryRoot "release"
$stagingRoot = Join-Path $releaseRoot "staging"
$applicationRoot = Join-Path $stagingRoot "Correntra"
$velopackRoot = Join-Path $releaseRoot "velopack"
$runtime = "win-x64"

function Assert-RepositoryChild([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the repository: $fullPath"
    }

    return $fullPath
}

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

Set-Location $repositoryRoot
Assert-RepositoryChild $releaseRoot | Out-Null

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
if (Test-Path -LiteralPath $velopackRoot) {
    Remove-Item -LiteralPath $velopackRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $applicationRoot, $velopackRoot | Out-Null

Invoke-Checked "dotnet tool restore" { dotnet tool restore }
Invoke-Checked "dotnet restore" { dotnet restore Correntra.sln --locked-mode }
New-Item -ItemType Directory -Force -Path (Join-Path $repositoryRoot "artifacts\sbom") | Out-Null
Invoke-Checked "SBOM generation" {
    dotnet CycloneDX Correntra.sln -o artifacts\sbom --json --exclude-dev
}
& (Join-Path $PSScriptRoot "check-licenses.ps1")

# Self-contained publish needs RID-specific assets; regenerate the lock file
# for win-x64 here. The repository lock files are restored to their RID-less
# state afterwards by the release operator (git checkout).
Invoke-Checked "rid restore" { dotnet restore Correntra.sln -r $runtime --force-evaluate }

if (-not $SkipTests) {
    Invoke-Checked "dotnet tests" { dotnet test Correntra.sln -c Release --no-restore }
}

$publishArguments = @(
    "-c", "Release",
    "-r", $runtime,
    "--self-contained", "true",
    "--no-restore",
    "-p:Version=$Version",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:PublishTrimmed=false",
    "-o", $applicationRoot
)

Invoke-Checked "desktop publish" {
    dotnet publish "src/Correntra.Desktop/Correntra.Desktop.csproj" @publishArguments
}
Invoke-Checked "agent publish" {
    dotnet publish "src/Correntra.Agent/Correntra.Agent.csproj" @publishArguments
}

$vendorRoot = Join-Path $repositoryRoot "artifacts\vendor"
$ffmpegRoot = Join-Path $vendorRoot "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1"
if (-not $SkipFfmpegDownload -or -not (Test-Path -LiteralPath (Join-Path $ffmpegRoot "bin\ffmpeg.exe"))) {
    $resolvedFfmpeg = & (Join-Path $PSScriptRoot "get-ffmpeg.ps1")
    if ($resolvedFfmpeg) {
        $ffmpegRoot = [IO.Path]::GetFullPath(@($resolvedFfmpeg)[-1])
    }
}

$ffmpegTarget = Join-Path $applicationRoot "tools\ffmpeg"
New-Item -ItemType Directory -Force -Path $ffmpegTarget | Out-Null
Copy-Item -LiteralPath (Join-Path $ffmpegRoot "bin\ffmpeg.exe") -Destination $ffmpegTarget -Force
Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot "bin") -Filter "*.dll" | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $ffmpegTarget -Force
}
Copy-Item -LiteralPath (Join-Path $ffmpegRoot "LICENSE.txt") -Destination (Join-Path $ffmpegTarget "LICENSE.txt") -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "packaging\FFMPEG-NOTICE.md") -Destination $ffmpegTarget -Force

# Social video engine: fetch yt-dlp (Unlicense) and place it next to the app so
# the agent can locate it. Failure only disables social-site extraction.
try {
    $resolvedYtDlp = & (Join-Path $PSScriptRoot "get-yt-dlp.ps1")
    if ($resolvedYtDlp) {
        Copy-Item -LiteralPath ([IO.Path]::GetFullPath(@($resolvedYtDlp)[-1])) -Destination (Join-Path $applicationRoot "yt-dlp.exe") -Force
    }
}
catch {
    Write-Warning "yt-dlp could not be prepared for the release: $($_.Exception.Message)"
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE.txt") -Destination $applicationRoot -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") -Destination $applicationRoot -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "artifacts\sbom\bom.json") -Destination (Join-Path $applicationRoot "sbom.cdx.json") -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "packaging\UZANTI-KURULUMU.txt") -Destination $applicationRoot -Force

# Ship the browser extension next to the app so the in-app setup wizard can
# point "Load unpacked" at it without any download. The per-run bridge token
# must NOT ship: it is a secret the agent regenerates on every start.
$stagedExtension = Join-Path $applicationRoot "browser-extension"
Copy-Item -LiteralPath (Join-Path $repositoryRoot "browser-extension") -Destination $stagedExtension -Recurse -Force
Remove-Item -LiteralPath (Join-Path $stagedExtension "bridge-token.txt") -Force -ErrorAction SilentlyContinue

$portablePath = Join-Path $releaseRoot "Correntra-Downloader-$Version-win-x64-portable.zip"
$sbomPath = Join-Path $releaseRoot "Correntra-Downloader-$Version-sbom.cdx.json"
if (Test-Path -LiteralPath $portablePath) {
    Remove-Item -LiteralPath $portablePath -Force
}
Compress-Archive -Path (Join-Path $applicationRoot "*") -DestinationPath $portablePath -CompressionLevel Optimal
Copy-Item -LiteralPath (Join-Path $repositoryRoot "artifacts\sbom\bom.json") -Destination $sbomPath -Force

$releaseNotesPath = Join-Path $repositoryRoot "packaging\RELEASE-NOTES-$Version.md"
if (-not (Test-Path $releaseNotesPath)) {
    $releaseNotesPath = Join-Path $repositoryRoot "packaging\RELEASE-NOTES-0.1.0.md"
}

Invoke-Checked "Velopack packaging" {
    dotnet vpk pack `
        --packId "Correntra.Downloader" `
        --packVersion $Version `
        --packDir $applicationRoot `
        --mainExe "Correntra.exe" `
        --runtime $runtime `
        --outputDir $velopackRoot `
        --packTitle "Correntra Downloader" `
        --packAuthors "Correntra" `
        --icon (Join-Path $repositoryRoot "src\Correntra.Desktop\Assets\correntra.ico") `
        --releaseNotes $releaseNotesPath `
        --instWelcome (Join-Path $repositoryRoot "packaging\INSTALLER-WELCOME.txt") `
        --instLicense (Join-Path $repositoryRoot "LICENSE.txt") `
        --instReadme (Join-Path $repositoryRoot "packaging\INSTALLER-README.md") `
        --shortcuts "Desktop,StartMenuRoot" `
        --yes
}

Get-ChildItem -LiteralPath $velopackRoot -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $releaseRoot -Force
}

$hashFile = Join-Path $releaseRoot "SHA256SUMS.txt"
$releaseFiles = Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name
$hashLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
Set-Content -LiteralPath $hashFile -Value $hashLines -Encoding ascii

Write-Host "Release artifacts created in $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | Select-Object Name, Length
