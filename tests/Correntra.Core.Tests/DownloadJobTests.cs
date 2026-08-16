using Correntra.Core.Downloads;
using Correntra.Core.Security;

namespace Correntra.Core.Tests;

public sealed class DownloadJobTests
{
    [Fact]
    public void CreateProducesPendingImmutableJobWithSafeTarget()
    {
        DownloadJob job = DownloadJob.Create(
            TestData.Source(),
            "folder/unsafe?.bin",
            TestData.DestinationDirectory,
            TestData.Timestamp,
            startImmediately: false);

        Assert.Equal(DownloadJobState.Pending, job.State);
        Assert.Equal(DownloadExecutionIntent.Hold, job.ExecutionIntent);
        Assert.Equal("folder_unsafe_.bin", job.FileName);
        Assert.Equal(1, job.AttemptNumber);
        Assert.False(job.IsTerminal);
        Assert.EndsWith(job.FileName, job.TargetPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullSuccessPathIsAllowed()
    {
        DownloadJob job = TestData.PendingJob()
            .TransitionTo(DownloadJobState.Probing, TestData.Timestamp.AddSeconds(1))
            .TransitionTo(DownloadJobState.Queued, TestData.Timestamp.AddSeconds(2))
            .TransitionTo(DownloadJobState.Downloading, TestData.Timestamp.AddSeconds(3))
            .ReportProgress(100, 100, TestData.Timestamp.AddSeconds(4))
            .TransitionTo(DownloadJobState.Verifying, TestData.Timestamp.AddSeconds(5))
            .TransitionTo(DownloadJobState.Finalizing, TestData.Timestamp.AddSeconds(6))
            .TransitionTo(DownloadJobState.Completed, TestData.Timestamp.AddSeconds(7));

        Assert.True(job.IsTerminal);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(100, job.BytesTransferred);
        Assert.Equal(100, job.TotalBytes);
    }

    [Theory]
    [InlineData(DownloadJobState.Pending, DownloadJobState.Completed)]
    [InlineData(DownloadJobState.Queued, DownloadJobState.Verifying)]
    [InlineData(DownloadJobState.Downloading, DownloadJobState.Completed)]
    [InlineData(DownloadJobState.Paused, DownloadJobState.Downloading)]
    [InlineData(DownloadJobState.Completed, DownloadJobState.Queued)]
    [InlineData(DownloadJobState.Failed, DownloadJobState.Queued)]
    [InlineData(DownloadJobState.Cancelled, DownloadJobState.Pending)]
    public void InvalidStateTransitionsAreRejected(DownloadJobState current, DownloadJobState next)
    {
        Assert.False(DownloadJobStateMachine.CanTransition(current, next));
        Assert.Throws<InvalidOperationException>(() => DownloadJobStateMachine.EnsureTransition(current, next));
    }

    [Fact]
    public void FailedStateRequiresFailureDetails()
    {
        DownloadJob job = TestData.PendingJob();

        Assert.Throws<ArgumentNullException>(() =>
            job.TransitionTo(DownloadJobState.Failed, TestData.Timestamp.AddSeconds(1)));

        DownloadJob failed = job.TransitionTo(
            DownloadJobState.Failed,
            TestData.Timestamp.AddSeconds(1),
            new DownloadFailure("network.timeout", "The connection timed out.", true));

        Assert.Equal("network.timeout", failed.Failure?.Code);
        Assert.True(failed.Failure?.IsRetryable);
    }

    [Fact]
    public void ProgressCannotMoveBackwardExceedTotalOrChangeKnownTotal()
    {
        DownloadJob job = TestData.DownloadingJob()
            .ReportProgress(10, 100, TestData.Timestamp.AddSeconds(4));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            job.ReportProgress(9, 100, TestData.Timestamp.AddSeconds(5)));
        Assert.Throws<ArgumentException>(() =>
            job.ReportProgress(101, 100, TestData.Timestamp.AddSeconds(5)));
        Assert.Throws<InvalidOperationException>(() =>
            job.ReportProgress(20, 200, TestData.Timestamp.AddSeconds(5)));
    }

    [Fact]
    public void PausingSetsHoldIntentAndRecoveryPreservesPause()
    {
        DownloadJob paused = TestData.DownloadingJob()
            .TransitionTo(DownloadJobState.Paused, TestData.Timestamp.AddSeconds(4));

        DownloadJob recovered = paused.RecoverAfterInterruption(TestData.Timestamp.AddSeconds(5));

        Assert.Equal(DownloadExecutionIntent.Hold, paused.ExecutionIntent);
        Assert.Equal(DownloadJobState.Paused, recovered.State);
    }

    [Fact]
    public void ActiveRunIntentRecoversToQueued()
    {
        DownloadJob recovered = TestData.DownloadingJob()
            .RecoverAfterInterruption(TestData.Timestamp.AddSeconds(4));

        Assert.Equal(DownloadJobState.Queued, recovered.State);
        Assert.Equal(DownloadExecutionIntent.RunWhenPossible, recovered.ExecutionIntent);
    }

    [Fact]
    public void RetryCreatesNewAttemptWithoutMutatingTerminalJob()
    {
        DownloadJob failed = TestData.PendingJob().TransitionTo(
            DownloadJobState.Failed,
            TestData.Timestamp.AddSeconds(1),
            new DownloadFailure("network", "Network error", true));

        DownloadJob retry = failed.CreateRetry(TestData.Timestamp.AddSeconds(2));

        Assert.Equal(failed.Id, retry.Id);
        Assert.Equal(1, failed.AttemptNumber);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.Equal(DownloadJobState.Pending, retry.State);
        Assert.Null(retry.Failure);
        Assert.Equal(0, retry.BytesTransferred);
    }

    [Fact]
    public void TimestampsMustBeUtcAndMonotonic()
    {
        Assert.Throws<ArgumentException>(() => DownloadJob.Create(
            TestData.Source(),
            "file.bin",
            TestData.DestinationDirectory,
            TestData.Timestamp.ToOffset(TimeSpan.FromHours(3))));

        DownloadJob job = TestData.PendingJob();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            job.TransitionTo(DownloadJobState.Probing, TestData.Timestamp.AddSeconds(-1)));
    }

    [Fact]
    public void DownloadSourceRejectsCredentialsEmbeddedInUrl()
    {
        Assert.Throws<ArgumentException>(() => new DownloadSource(new Uri("https://user:pass@example.test/file")));
    }

    [Fact]
    public void DownloadSourceCredentialsExpireAtBoundary()
    {
        HttpHeaderSet headers = new([new("Cookie", "sid=secret")]);
        DownloadSource source = new(
            new Uri("https://example.test/private"),
            headers: headers,
            credentialExpiresAtUtc: TestData.Timestamp.AddMinutes(5));

        Assert.True(source.ContainsCredentials);
        Assert.False(source.CredentialsAreExpired(TestData.Timestamp.AddMinutes(4)));
        Assert.True(source.CredentialsAreExpired(TestData.Timestamp.AddMinutes(5)));
    }
}
