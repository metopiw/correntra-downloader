using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;
using Xunit;

namespace Correntra.Desktop.Tests;

public sealed class MainViewModelStateMappingTests
{
    [Fact]
    public void ApplySnapshotMapsNeedsInputJobsToAwaitingConfirmationState()
    {
        var viewModel = new MainViewModel();

        viewModel.ApplyAgentSnapshot(CreateSnapshot(DownloadJobState.NeedsInput));

        DownloadListItem item = Assert.Single(viewModel.Downloads);
        Assert.Equal("State.NeedsInput", item.StateKey);
    }

    [Fact]
    public void ApplySnapshotKeepsQueuedJobsMappedToQueuedState()
    {
        var viewModel = new MainViewModel();

        viewModel.ApplyAgentSnapshot(CreateSnapshot(DownloadJobState.Queued));

        DownloadListItem item = Assert.Single(viewModel.Downloads);
        Assert.Equal("State.Queued", item.StateKey);
    }

    [Fact]
    public void ApplySnapshotTreatsRecentExtensionHeartbeatAsConnected()
    {
        var viewModel = new MainViewModel();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        viewModel.ApplyAgentSnapshot(CreateSnapshot(
            DownloadJobState.Queued,
            now,
            now.AddSeconds(-20)));

        Assert.True(viewModel.IsBrowserCaptureConnected);
        Assert.False(viewModel.IsBrowserCaptureDisconnected);
    }

    [Fact]
    public void ApplySnapshotExpiresStaleExtensionHeartbeat()
    {
        var viewModel = new MainViewModel();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        viewModel.ApplyAgentSnapshot(CreateSnapshot(
            DownloadJobState.Queued,
            now,
            now.AddMinutes(-2)));

        Assert.False(viewModel.IsBrowserCaptureConnected);
        Assert.True(viewModel.IsBrowserCaptureDisconnected);
    }

    private static AgentSnapshot CreateSnapshot(
        DownloadJobState state,
        DateTimeOffset? generatedAtUtc = null,
        DateTimeOffset? browserExtensionLastSeenUtc = null)
    {
        DateTimeOffset generated = generatedAtUtc ?? DateTimeOffset.UtcNow;
        return new AgentSnapshot(
            generated,
            [
                new DownloadJobSnapshot(
                    JobId.Create(),
                    1,
                    "archive.zip",
                    Path.GetTempPath(),
                    "https://example.test/archive.zip",
                    state,
                    0,
                    null,
                    generated),
            ],
            browserExtensionLastSeenUtc: browserExtensionLastSeenUtc);
    }
}
