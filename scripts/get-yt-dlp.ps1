[CmdletBinding()]
param(
    [string]$DestinationRoot = "artifacts/vendor",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$exeName = "yt-dlp.exe"
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\$DestinationRoot"))
$exePath = Join-Path $destination $exeName

New-Item -ItemType Directory -Force -Path $destination | Out-Null

if ($Force -or -not (Test-Path -LiteralPath $exePath)) {
    # Prefer the latest stable release; the rolling "latest" asset is the
    # fallback so the gate keeps working when a pinned tag is pruned.
    $urls = @(
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/$exeName",
        "https://github.com/yt-dlp/yt-dlp/releases/download/2025.06.30/$exeName"
    )
    $downloaded = $false
    foreach ($url in $urls) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $exePath
            $downloaded = $true
            break
        }
        catch {
            Write-Warning "yt-dlp indirilemedi ($url): $($_.Exception.Message)"
        }
    }

    if (-not $downloaded) {
        throw "yt-dlp.exe could not be downloaded from any known release URL."
    }
}

# Verify the binary actually runs and report its version for traceability.
$versionOutput = (& $exePath --version 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "The downloaded yt-dlp binary did not report its version: $versionOutput"
}

Write-Output $exePath
