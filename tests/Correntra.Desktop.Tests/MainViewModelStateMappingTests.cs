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

    private static AgentSnapshot CreateSnapshot(DownloadJobState state)
    {
        return new AgentSnapshot(
            DateTimeOffset.UtcNow,
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
                    DateTimeOffset.UtcNow),
            ]);
    }
}