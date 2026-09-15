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

    [Fact]
    public async Task OvershotProgressRowIsClampedInsteadOfHidingNeedsInputJobs()
    {
        // Live bug: a playlist row stored bytes_transferred (170 MB) above its
        // estimated total_bytes (12 MB). Record validation rejects bytes >
        // total, so ListAsync threw, the snapshot came back Rejected, HTTP
        // /jobs went empty and the desktop poll loop died — every
        // save-confirmation dialog silently stopped appearing.
        await using var fixture = new RepositoryFixture();
        AgentJobRepository repository = fixture.CreateRepository();
        await repository.InitializeAsync();
        AgentJobRecord healthy = await repository.CreateAsync(
            new AgentJobCreation(
                new Uri("https://example.test/clip.mp4"),
                "clip.mp4",
                fixture.DownloadDirectory,
                StartImmediately: false,
                NeedsUserConfirmation: true),
            DateTimeOffset.UtcNow);

        AgentJobRecord finished = await repository.CreateAsync(
            new AgentJobCreation(
                new Uri("https://example.test/set.zip"),
                "set.zip",
                fixture.DownloadDirectory,
                StartImmediately: true,
                NeedsUserConfirmation: false),
            DateTimeOffset.UtcNow);
        await using (SqliteConnection connection = await fixture.OpenAsync())
        {
            await using SqliteCommand poison = connection.CreateCommand();
            poison.CommandText = "UPDATE jobs SET bytes_transferred = 170844486, total_bytes = 12641747 WHERE id = $id;";
            poison.Parameters.AddWithValue("$id", finished.Id.ToString());
            Assert.Equal(1, await poison.ExecuteNonQueryAsync());
        }

        IReadOnlyList<AgentJobRecord> jobs = await repository.ListAsync();

        Assert.Contains(jobs, job => job.Id == healthy.Id && job.State == DownloadJobState.NeedsInput);
        AgentJobRecord widened = Assert.Single(jobs, job => job.Id == finished.Id);
        Assert.Equal(170844486, widened.BytesTransferred);
        Assert.Equal(170844486, widened.TotalBytes);
    }

    [Fact]
    public async Task MalformedRowIsSkippedInsteadOfHidingHealthyJobs()
    {
        await using var fixture = new RepositoryFixture();
        AgentJobRepository repository = fixture.CreateRepository();
        await repository.InitializeAsync();
        AgentJobRecord healthy = await repository.CreateAsync(
            new AgentJobCreation(
                new Uri("https://example.test/clip.mp4"),
                "clip.mp4",
                fixture.DownloadDirectory,
                StartImmediately: false,
                NeedsUserConfirmation: true),
            DateTimeOffset.UtcNow);

        await using (SqliteConnection connection = await fixture.OpenAsync())
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO jobs(
                    id, attempt, source_url, request_method, file_name, destination_directory,
                    category_id, queue_id, priority, state, execution_intent, created_at_utc,
                    updated_at_utc, bytes_transferred, total_bytes, failure_code, failure_message,
                    checkpoint_path, row_version)
                VALUES(
                    $id, 1, 'https://example.test/legacy.bin', 0, 'legacy.bin', $destination,
                    NULL, NULL, 1, 9, 0, 'not-a-timestamp',
                    $updated, 0, NULL, NULL, NULL, NULL, 1);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$destination", fixture.DownloadDirectory);
            insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        IReadOnlyList<AgentJobRecord> jobs = await repository.ListAsync();

        Assert.Contains(jobs, job => job.Id == healthy.Id);
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

        public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(_root, "correntra.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared,
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

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
