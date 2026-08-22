$ErrorActionPreference = "Stop"
Get-Process -Name "Correntra","Correntra.Agent" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
& "C:\Program Files\dotnet\dotnet.exe" build "D:\ai\download_manager\Correntra.sln" -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "C:\Program Files\dotnet\dotnet.exe" test "D:\ai\download_manager\Correntra.sln" -c Debug --no-build
exit $LASTEXITCODE
