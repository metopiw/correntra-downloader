using Correntra.Core.Downloads;
using Correntra.Core.Security;

namespace Correntra.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Timestamp = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    public static string DestinationDirectory => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "correntra-core-tests"));

    public static DownloadSource Source(
        string url = "https://downloads.example.test/file.bin",
        HttpHeaderSet? headers = null)
    {
        return new DownloadSource(new Uri(url), headers: headers);
    }

    public static DownloadJob PendingJob(bool startImmediately = true)
    {
        return DownloadJob.Create(Source(), "file.bin", DestinationDirectory, Timestamp, startImmediately);
    }

    public static DownloadJob DownloadingJob()
    {
        return PendingJob()
            .TransitionTo(DownloadJobState.Probing, Timestamp.AddSeconds(1))
            .TransitionTo(DownloadJobState.Queued, Timestamp.AddSeconds(2))
            .TransitionTo(DownloadJobState.Downloading, Timestamp.AddSeconds(3));
    }
}
