# End-to-end media download test over the loopback bridge.
# Creates a real yt-dlp-backed job for a short public video, confirms it and
# polls until a terminal state. Exit code 0 means the job completed.
[CmdletBinding()]
param(
    [string]$Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw",
    [string]$FormatId = "bestvideo[height<=360]+bestaudio/best[height<=360]",
    [string]$Title = "media e2e"
)

$body = @{
    url = $Url
    pageUrl = $Url
    referrer = $Url
    title = $Title
    media = @{
        kind = "video"
        title = $Title
        container = "mp4"
        formatId = $FormatId
    }
} | ConvertTo-Json -Depth 4

$created = Invoke-RestMethod -Uri "http://127.0.0.1:27410/media/start" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 30
Write-Output ("media/start response: " + ($created | ConvertTo-Json -Compress))

$jobId = $created.jobId
if (-not $jobId) {
    Write-Output "NO-JOB"
    exit 3
}

$confirmBody = @{ jobId = $jobId; startImmediately = $true } | ConvertTo-Json
$confirmed = Invoke-RestMethod -Uri "http://127.0.0.1:27410/confirm" -Method Post -Body $confirmBody -ContentType "application/json" -TimeoutSec 10
Write-Output ("confirm response: " + ($confirmed | ConvertTo-Json -Compress))

$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 4
    $jobs = Invoke-RestMethod -Uri "http://127.0.0.1:27410/jobs" -TimeoutSec 10
    foreach ($job in $jobs.jobs) {
        $thisJobId = $job.jobId
        if (-not $thisJobId) { $thisJobId = $job.id }
        if ($thisJobId -ne $jobId) { continue }
        Write-Output ("state=" + $job.state + " file=" + $job.fileName + " bytes=" + $job.bytesTransferred + "/" + $job.totalBytes)
        if ($job.state -eq 9) { exit 0 }
        if ($job.state -in 10, 11) { exit 2 }
    }
}
Write-Output "TIMEOUT"
exit 4
