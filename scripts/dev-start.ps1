[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $repositoryRoot

if (-not $SkipBuild) {
    dotnet restore Correntra.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

    dotnet build Correntra.sln -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

    $npm = Get-Command npm -ErrorAction SilentlyContinue
    $extensionBuilt = Test-Path (Join-Path $repositoryRoot "browser-extension\dist\manifest.json")
    if ($npm) {
        Push-Location browser-extension
        try {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
            npm run build
            if ($LASTEXITCODE -ne 0) { throw "Browser extension build failed." }
        }
        finally {
            Pop-Location
        }
    }
    elseif ($extensionBuilt) {
        Write-Warning "npm bulunamadi; mevcut browser-extension/dist kullanilacak."
    }
    else {
        throw "npm bulunamadi ve browser-extension/dist mevcut degil. Node.js kurun veya extension'i elle derleyin."
    }

    # Social video engine (yt-dlp): download once into artifacts/vendor, then
    # copy next to the agent/desktop binaries. Failure only disables the
    # social-site extractor; normal downloads keep working.
    $ytDlpVendor = Join-Path $repositoryRoot "artifacts\vendor\yt-dlp.exe"
    if (-not (Test-Path $ytDlpVendor)) {
        try {
            & (Join-Path $repositoryRoot "scripts\get-yt-dlp.ps1") | Out-Null
        }
        catch {
            Write-Warning "yt-dlp alinamadi; sosyal medya video indirme devre disi kalacak. Hata: $($_.Exception.Message)"
        }
    }

    if (Test-Path $ytDlpVendor) {
        foreach ($relative in @(
            "src\Correntra.Agent\bin\Debug\net8.0-windows10.0.17763.0",
            "src\Correntra.Desktop\bin\Debug\net8.0-windows10.0.17763.0")) {
            $outputDirectory = Join-Path $repositoryRoot $relative
            if (Test-Path $outputDirectory) {
                $vendorDirectory = Join-Path $outputDirectory "vendor"
                New-Item -ItemType Directory -Force -Path $vendorDirectory | Out-Null
                Copy-Item $ytDlpVendor (Join-Path $vendorDirectory "yt-dlp.exe") -Force
            }
        }
    }

    # Deploy the LGPL FFmpeg sidecar (HLS/DASH remux + yt-dlp track merging)
    # next to both binaries when a vendored build is available.
    $ffmpegSource = Get-ChildItem (Join-Path $repositoryRoot "artifacts\vendor") -Recurse -Filter "ffmpeg.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($ffmpegSource) {
        foreach ($relative in @(
            "src\Correntra.Agent\bin\Debug\net8.0-windows10.0.17763.0",
            "src\Correntra.Desktop\bin\Debug\net8.0-windows10.0.17763.0")) {
            $outputDirectory = Join-Path $repositoryRoot $relative
            if (Test-Path $outputDirectory) {
                Copy-Item $ffmpegSource.FullName (Join-Path $outputDirectory "ffmpeg.exe") -Force
                Get-ChildItem $ffmpegSource.DirectoryName -Filter "*.dll" -ErrorAction SilentlyContinue |
                    ForEach-Object { Copy-Item $_.FullName (Join-Path $outputDirectory $_.Name) -Force }
            }
        }
    }
}

$agent = Join-Path $repositoryRoot "src\Correntra.Agent\bin\Debug\net8.0-windows10.0.17763.0\Correntra.Agent.exe"
$desktop = Join-Path $repositoryRoot "src\Correntra.Desktop\bin\Debug\net8.0-windows10.0.17763.0\Correntra.exe"

& (Join-Path $repositoryRoot "scripts\register-native-host.ps1")
if ($LASTEXITCODE -ne 0) { throw "Native Messaging host registration failed." }

if (-not (Get-Process -Name "Correntra.Agent" -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $agent -WindowStyle Hidden
}

Start-Process -FilePath $desktop

