[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishRoot = Join-Path $repositoryRoot "artifacts\correntra-start-launcher"
$launcherProject = Join-Path $repositoryRoot "tools\Correntra.StartLauncher\Correntra.StartLauncher.csproj"
$outputPath = Join-Path $repositoryRoot "Correntra Baslat.exe"

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

& dotnet publish $launcherProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Correntra launcher EXE could not be created."
}

Copy-Item -LiteralPath (Join-Path $publishRoot "CorrentraBaslat.exe") -Destination $outputPath -Force
Write-Output $outputPath
