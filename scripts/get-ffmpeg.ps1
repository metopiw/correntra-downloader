[CmdletBinding()]
param(
    [string]$DestinationRoot = "artifacts/vendor",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
# Pinned to an immutable dated autobuild tag (NOT the rolling "latest" tag,
# whose assets are replaced in place and break checksum verification).
# To upgrade: pick a new tag from BtbN/FFmpeg-Builds, update all three values
# below, verify the LGPL license gate still passes, then run a release.
$releaseTag = "autobuild-2026-08-24-13-10"
$archiveName = "ffmpeg-n8.1.2-44-g7c533d0f86-win64-lgpl-shared-8.1.zip"
$archiveUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$releaseTag/$archiveName"
$expectedSha256 = "60AA2BE28B1BB7B95C397DDD4EEA4EF464193D2EACAF0B865B40CC976CCB4DB0"
$folderName = "ffmpeg-n8.1.2-44-g7c533d0f86-win64-lgpl-shared-8.1"
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\$DestinationRoot"))
$archivePath = Join-Path $destination $archiveName
$expandedRoot = Join-Path $destination $folderName

New-Item -ItemType Directory -Force -Path $destination | Out-Null

if ($Force -or -not (Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath
}

$actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "FFmpeg archive checksum mismatch. Expected $expectedSha256, got $actualSha256. The rolling upstream asset may have changed; review and update the pinned build intentionally."
}

if ($Force -and (Test-Path -LiteralPath $expandedRoot)) {
    Remove-Item -LiteralPath $expandedRoot -Recurse -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $expandedRoot "bin\ffmpeg.exe"))) {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $destination -Force
}

$ffmpegPath = Join-Path $expandedRoot "bin\ffmpeg.exe"
$versionOutput = (& $ffmpegPath -hide_banner -version 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "The pinned FFmpeg binary did not report its version." }
$buildOutput = (& $ffmpegPath -hide_banner -buildconf 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "The pinned FFmpeg binary did not report its build configuration." }
$licenseOutput = (& $ffmpegPath -hide_banner -L 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "The pinned FFmpeg binary did not report its license." }
$inspection = $versionOutput, $buildOutput, $licenseOutput -join "`n"

if ($inspection -match "--enable-gpl" -or $inspection -match "--enable-nonfree") {
    throw "Release gate rejected the FFmpeg build because GPL or nonfree configuration was detected."
}

if ($inspection -notmatch "GNU Lesser General Public License") {
    throw "Release gate could not verify an LGPL license statement from FFmpeg."
}

Write-Output $expandedRoot
