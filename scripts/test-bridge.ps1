$body = @{
    url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
    finalUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
    filename = "download"
    headers = @{ "User-Agent" = "Mozilla/5.0 bridge-test" }
} | ConvertTo-Json -Depth 4

# The agent provisions a shared token into the extension folder on start;
# command endpoints reject requests without it (401) when one is present.
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$tokenFile = Join-Path $repoRoot "browser-extension\bridge-token.txt"
$headers = @{}
if (Test-Path -LiteralPath $tokenFile) {
    $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
    if ($token) { $headers["X-Correntra-Token"] = $token }
}

$created = Invoke-RestMethod -Uri "http://127.0.0.1:27410/takeover" -Method Post -Body $body -ContentType "application/json" -Headers $headers -TimeoutSec 15
Write-Output ("takeover response: " + ($created | ConvertTo-Json -Compress))

if ($created.jobId) {
    $confirmBody = @{ jobId = $created.jobId; startImmediately = $true } | ConvertTo-Json
    $confirmed = Invoke-RestMethod -Uri "http://127.0.0.1:27410/confirm" -Method Post -Body $confirmBody -ContentType "application/json" -Headers $headers -TimeoutSec 10
    Write-Output ("confirm response: " + ($confirmed | ConvertTo-Json -Compress))
}

$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $jobs = Invoke-RestMethod -Uri "http://127.0.0.1:27410/jobs" -TimeoutSec 10
    foreach ($job in $jobs.jobs) {
        if ($job.state -in 9, 10, 11) {
            Write-Output ("FINAL state=" + $job.state + " file=" + $job.fileName + " bytes=" + $job.bytesTransferred + "/" + $job.totalBytes)
            exit 0
        } else {
            Write-Output ("state=" + $job.state + " file=" + $job.fileName + " bytes=" + $job.bytesTransferred + "/" + $job.totalBytes)
        }
    }
}
Write-Output "TIMEOUT"
exit 2
