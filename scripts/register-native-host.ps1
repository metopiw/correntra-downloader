[CmdletBinding()]
param(
    [string]$NativeHostExe = "",
    [string]$ExtensionId = ""
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$hostName = "com.correntra.downloader"
$extensionManifestPath = Join-Path $root "browser-extension\dist\manifest.json"

if (-not (Test-Path -LiteralPath $extensionManifestPath -PathType Leaf)) {
    throw "Chrome extension manifest bulunamadı: $extensionManifestPath"
}

# The manifest contains a public key. Chrome derives the stable extension ID
# from SHA-256(public-key), first 16 bytes, with 0..15 mapped to a..p.
if ([string]::IsNullOrWhiteSpace($ExtensionId)) {
    $extensionManifest = Get-Content -LiteralPath $extensionManifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$extensionManifest.key)) {
        throw "browser-extension\dist\manifest.json içinde public key yok."
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash([Convert]::FromBase64String([string]$extensionManifest.key))
    }
    finally {
        $sha.Dispose()
    }

    $chars = foreach ($byte in $digest[0..15]) {
        $b = [int]$byte
        [char]([int][char]'a' + (($b -shr 4) -band 0x0F))
        [char]([int][char]'a' + ($b -band 0x0F))
    }
    $extensionId = -join $chars
}
elseif ($ExtensionId -notmatch '^[a-p]{32}$') {
    throw "ExtensionId geçersiz: $ExtensionId"
}

if ([string]::IsNullOrWhiteSpace($NativeHostExe)) {
    $candidates = @(
        (Join-Path $root "src\Correntra.NativeHost\bin\Debug\net8.0-windows10.0.17763.0\Correntra.NativeHost.exe"),
        (Join-Path $root "src\Correntra.NativeHost\bin\Release\net8.0-windows10.0.17763.0\Correntra.NativeHost.exe"),
        (Join-Path $root "src\Correntra.NativeHost\bin\Debug\net8.0-windows10.0.17763.0\win-x64\Correntra.NativeHost.exe"),
        (Join-Path $root "src\Correntra.NativeHost\bin\Release\net8.0-windows10.0.17763.0\win-x64\Correntra.NativeHost.exe"),
        (Join-Path $root "Correntra.NativeHost.exe")
    )
    $NativeHostExe = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

if (-not $NativeHostExe -or -not (Test-Path -LiteralPath $NativeHostExe -PathType Leaf)) {
    throw "Correntra.NativeHost.exe bulunamadı. Önce solution'ı build edin veya -NativeHostExe ile tam yolu verin."
}

$NativeHostExe = [IO.Path]::GetFullPath($NativeHostExe)
$manifestDir = Join-Path $env:LOCALAPPDATA "Correntra\Browser"
$manifestPath = Join-Path $manifestDir "$hostName.json"
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null

$manifest = [ordered]@{
    name = $hostName
    description = "Correntra Downloader Native Messaging Host"
    path = $NativeHostExe
    type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}

$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

$chromeKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
$edgeKey = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
foreach ($key in @($chromeKey, $edgeKey)) {
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name "(Default)" -Value $manifestPath -PropertyType String -Force | Out-Null
}

# Read back the exact values we just wrote. This catches path/registry-provider
# mistakes immediately instead of letting Chrome silently report a missing host.
$registryValue = (Get-ItemProperty -LiteralPath $chromeKey -Name "(Default)")."(Default)"
if ($registryValue -ne $manifestPath) {
    throw "Chrome Native Messaging registry kaydı doğrulanamadı: $registryValue"
}

$manifestCheck = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifestCheck.name -ne $hostName) {
    throw "Native Messaging manifest name doğrulaması başarısız."
}
if ([IO.Path]::GetFullPath([string]$manifestCheck.path) -ne $NativeHostExe) {
    throw "Native Messaging manifest path doğrulaması başarısız."
}
if (-not ($manifestCheck.allowed_origins -contains "chrome-extension://$extensionId/")) {
    throw "Manifest allowed_origins içinde mevcut extension ID yok."
}

Write-Host "Native Messaging host kaydedildi ve doğrulandı."
Write-Host "  Host:       $hostName"
Write-Host "  Manifest:   $manifestPath"
Write-Host "  Exe:        $NativeHostExe"
Write-Host "  Extension:  chrome-extension://$extensionId/"
Write-Host "  Chrome key: $chromeKey"
Write-Host "  Edge key:   $edgeKey"
