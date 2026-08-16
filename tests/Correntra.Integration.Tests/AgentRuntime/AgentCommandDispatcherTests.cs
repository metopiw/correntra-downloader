using System.Text.Json;
using Correntra.Agent.Runtime;
using Correntra.Core.Downloads;
using Correntra.Infrastructure.Storage;
using Correntra.Transfer;
using Microsoft.Data.Sqlite;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class AgentCommandDispatcherTests
{
    [Fact]
    public async Task TakeoverPersistsNeedsInputBeforeAcknowledging()
    {
        await using var fixture = new DispatcherFixture();
        await fixture.InitializeAsync();
        AgentRequestEnvelope request = CreateRequest(
            "takeover.offer",
            new
            {
                url = "https://example.test/files/tool.exe",
                filename = "tool.exe",
                headers = new { Referer = "https://example.test/download" },
            });

        AgentResponseEnvelope response = await fixture.Dispatcher.DispatchAsync(request);

        Assert.True(response.Payload.Accepted);
        Assert.True(fixture.DesktopLauncher.WasCalled);
        AgentJobRecord job = Assert.Single(await fixture.Coordinator.ListAsync());
        Assert.Equal(DownloadJobState.NeedsInput, job.State);
        Assert.Equal("tool.exe", job.FileName);
        Assert.Equal(response.Payload.JobId, job.Id.ToString());
    }

    [Fact]
    public async Task RemoveDeletesCancelledJobFromRepository()
    {
        await using var fixture = new DispatcherFixture();
        await fixture.InitializeAsync();
        AgentResponseEnvelope created = await fixture.Dispatcher.DispatchAsync(CreateRequest(
            "takeover.offer",
            new
            {
                url = "https://example.test/files/config.zip",
                filename = "config.zip",
            }));

        string jobId = created.Payload.JobId!;
        AgentResponseEnvelope cancelled = await fixture.Dispatcher.DispatchAsync(CreateRequest(
            "download.cancel",
            new { jobId }));

        Assert.True(cancelled.Payload.Accepted);

        AgentResponseEnvelope removed = await fixture.Dispatcher.DispatchAsync(CreateRequest(
            "download.remove",
            new { jobId, deleteDownloadedFile = true }));

        Assert.True(removed.Payload.Accepted);
        Assert.Empty(await fixture.Coordinator.ListAsync());
    }

    [Fact]
    public async Task ConfirmLaterMovesNeedsInputToPausedWithoutStartingNetwork()
    {
        await using var fixture = new DispatcherFixture();
        await fixture.InitializeAsync();
        AgentResponseEnvelope created = await fixture.Dispatcher.DispatchAsync(CreateRequest(
            "media.start",
            new
            {
                candidateId = "c_1234567890123456789012",
                url = "https://example.test/stream/video.mp4",
                media = new { kind = "video", title = "Demo", container = "mp4" },
            }));

        AgentResponseEnvelope confirmed = await fixture.Dispatcher.DispatchAsync(CreateRequest(
            "download.confirm",
            new { jobId = created.Payload.JobId, startImmediately = false }));

        Assert.True(confirmed.Payload.Accepted);
        AgentJobRecord job = Assert.Single(await fixture.Coordinator.ListAsync());
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.Equal(DownloadExecutionIntent.Hold, job.ExecutionIntent);
    }

    private static AgentRequestEnvelope CreateRequest(string kind, object payload)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new AgentRequestEnvelope(1, kind, "r_test", DateTimeOffset.UtcNow, document.RootElement.Clone());
    }

    private sealed class DispatcherFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Correntra.Integration.Tests",
            Guid.NewGuid().ToString("N"));

        public DispatcherFixture()
        {
            var repository = new AgentJobRepository(
                new CorrentraDatabase(Path.Combine(_root, "correntra.db")),
                new PassthroughProtector());
            Coordinator = new DownloadJobCoordinator(repository, new HttpTransferEngine(), 1);
            DesktopLauncher = new RecordingDesktopLauncher();
            Dispatcher = new AgentCommandDispatcher(Coordinator, DesktopLauncher);
        }

        public DownloadJobCoordinator Coordinator { get; }

        public RecordingDesktopLauncher DesktopLauncher { get; }

        public AgentCommandDispatcher Dispatcher { get; }

        public Task InitializeAsync() => Coordinator.InitializeAsync();

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class RecordingDesktopLauncher : IDesktopLauncher
    {
        public bool WasCalled { get; private set; }

        public Task<bool> ShowDownloadConfirmationAsync(
            Correntra.Core.JobId jobId,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class PassthroughProtector : IJobPayloadProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> payload) => payload.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload) => protectedPayload.ToArray();
    }
}
