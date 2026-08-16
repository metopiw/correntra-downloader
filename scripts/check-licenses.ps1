[CmdletBinding()]
param(
    [string]$BomPath = "artifacts/sbom/bom.json",
    [string]$PackageLockPath = "browser-extension/package-lock.json"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedBom = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $BomPath))
$resolvedLock = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageLockPath))
$forbidden = '(?i)(^|[^L])GPL|AGPL|SSPL|non[- ]?commercial|proprietary'
$reviewedMissing = @{
    "Avalonia.Angle.Windows.Natives" = "BSD-3-Clause (license file in pinned NuGet package)"
    "System.Memory" = "MIT (pinned Microsoft corefx package license URL)"
}

$bom = Get-Content -LiteralPath $resolvedBom -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()
foreach ($component in $bom.components) {
    $licenses = @($component.licenses | ForEach-Object {
        if ($_.license.id) { $_.license.id } elseif ($_.license.name) { $_.license.name }
    })
    if ($licenses.Count -eq 0 -and -not $reviewedMissing.ContainsKey($component.name)) {
        $failures.Add("Unknown .NET dependency license: $($component.name) $($component.version)")
    }
    if (($licenses -join ',') -match $forbidden) {
        $failures.Add("Forbidden .NET dependency license: $($component.name) $($component.version) [$($licenses -join ', ')]")
    }
}

$lock = Get-Content -LiteralPath $resolvedLock -Raw | ConvertFrom-Json
$lockEntries = @($lock.packages.PSObject.Properties)
foreach ($entry in $lockEntries) {
    if ([string]::IsNullOrEmpty($entry.Name)) { continue }
    $license = $entry.Value.license
    if ([string]::IsNullOrWhiteSpace($license)) {
        $failures.Add("Unknown extension dependency license: $($entry.Name)")
    }
    elseif ($license -match $forbidden) {
        $failures.Add("Forbidden extension dependency license: $($entry.Name) [$license]")
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host "License gate passed: $($bom.components.Count) .NET packages and $($lockEntries.Count) extension packages checked."

