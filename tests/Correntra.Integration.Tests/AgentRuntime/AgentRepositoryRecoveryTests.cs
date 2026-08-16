using Correntra.Agent.Runtime;
using Correntra.Core.Downloads;
using Correntra.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class AgentRepositoryRecoveryTests
{
    [Fact]
    public async Task RecoversDownloadingJobBackToDurableQueue()
    {
        await using var fixture = new RepositoryFixture();
        AgentJobRepository repository = fixture.CreateRepository();
        await repository.InitializeAsync();
        AgentJobRecord created = await repository.CreateAsync(
            new AgentJobCreation(
                new Uri("https://example.test/archive.zip"),
                "archive.zip",
                fixture.DownloadDirectory,
                StartImmediately: true,
                NeedsUserConfirmation: false),
            DateTimeOffset.UtcNow);

        AgentJobRecord? claimed = await repository.TryClaimNextAsync();
        Assert.Equal(created.Id, claimed?.Id);
        Assert.Equal(DownloadJobState.Downloading, claimed?.State);

        AgentJobRepository reopened = fixture.CreateRepository();
        await reopened.InitializeAsync();
        Assert.Equal(1, await reopened.RecoverInterruptedAsync(DateTimeOffset.UtcNow.AddSeconds(1)));

        AgentJobRecord? recovered = await reopened.GetAsync(created.Id);
        Assert.NotNull(recovered);
        Assert.Equal(DownloadJobState.Queued, recovered.State);
        Assert.Equal(DownloadExecutionIntent.RunWhenPossible, recovered.ExecutionIntent);
    }

    [Fact]
    public async Task PreservesNeedsInputAcrossRecovery()
    {
        await using var fixture = new RepositoryFixture();
        AgentJobRepository repository = fixture.CreateRepository();
        await repository.InitializeAsync();
        AgentJobRecord created = await repository.CreateAsync(
            new AgentJobCreation(
                new Uri("https://example.test/video.mp4"),
                "video.mp4",
                fixture.DownloadDirectory,
                StartImmediately: false,
                NeedsUserConfirmation: true),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, await repository.RecoverInterruptedAsync(DateTimeOffset.UtcNow.AddSeconds(1)));
        AgentJobRecord? recovered = await repository.GetAsync(created.Id);
        Assert.Equal(DownloadJobState.NeedsInput, recovered?.State);
    }

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Correntra.Integration.Tests",
            Guid.NewGuid().ToString("N"));

        public string DownloadDirectory => Path.Combine(_root, "downloads");

        public AgentJobRepository CreateRepository() => new(
            new CorrentraDatabase(Path.Combine(_root, "correntra.db")),
            new PassthroughProtector());

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughProtector : IJobPayloadProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> payload) => payload.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload) => protectedPayload.ToArray();
    }
}
