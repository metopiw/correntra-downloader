[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$hostName = "com.correntra.downloader"
$manifestPath = Join-Path $env:LOCALAPPDATA "Correntra\Browser\$hostName.json"
$extensionManifestPath = Join-Path $root "browser-extension\dist\manifest.json"
$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"

function Fail([string]$Message) { throw "QC FAIL: $Message" }

if (-not (Test-Path -LiteralPath $extensionManifestPath -PathType Leaf)) { Fail "Extension manifest yok: $extensionManifestPath" }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { Fail "Native host manifest yok: $manifestPath. Önce register-native-host.ps1 çalıştırın." }
if (-not (Test-Path -LiteralPath $registryPath)) { Fail "Chrome registry kaydı yok: $registryPath" }

$extensionManifest = Get-Content -LiteralPath $extensionManifestPath -Raw | ConvertFrom-Json
$nativeManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$registryValue = (Get-ItemProperty -LiteralPath $registryPath -Name "(Default)")."(Default)"

if ([string]::IsNullOrWhiteSpace([string]$extensionManifest.key)) { Fail "Extension manifest key yok." }
$sha = [Security.Cryptography.SHA256]::Create()
try { $digest = $sha.ComputeHash([Convert]::FromBase64String([string]$extensionManifest.key)) }
finally { $sha.Dispose() }
$chars = foreach ($byte in $digest[0..15]) {
    $b = [int]$byte
    [char]([int][char]'a' + (($b -shr 4) -band 0x0F))
    [char]([int][char]'a' + ($b -band 0x0F))
}
$extensionId = -join $chars

if ($registryValue -ne $manifestPath) { Fail "Registry manifest yolu yanlış: $registryValue" }
if ($nativeManifest.name -ne $hostName) { Fail "Host name yanlış: $($nativeManifest.name)" }
if (-not [IO.Path]::IsPathFullyQualified([string]$nativeManifest.path)) { Fail "Native host exe yolu absolute değil." }
if (-not (Test-Path -LiteralPath ([string]$nativeManifest.path) -PathType Leaf)) { Fail "Native host exe yok: $($nativeManifest.path)" }
if ($nativeManifest.allowed_origins -notcontains "chrome-extension://$extensionId/") { Fail "allowed_origins extension ID ile eşleşmiyor." }

Write-Host "QC PASS"
Write-Host "Extension ID : $extensionId"
Write-Host "Host         : $hostName"
Write-Host "Manifest     : $manifestPath"
Write-Host "NativeHost   : $($nativeManifest.path)"
Write-Host "Registry     : $registryPath"
