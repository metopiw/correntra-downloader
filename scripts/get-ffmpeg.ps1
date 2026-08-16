[CmdletBinding()]
param(
    [string]$DestinationRoot = "artifacts/vendor",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$archiveName = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"
$archiveUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$archiveName"
$expectedSha256 = "C0692B85D56F2995656406425C095700117DFD7A84F8CA5AF75EBF92ED08B8A9"
$folderName = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1"
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
