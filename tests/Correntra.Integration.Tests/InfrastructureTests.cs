using System.Buffers.Binary;
using System.Text.Json;
using Correntra.Infrastructure.Ipc;
using Correntra.Infrastructure.Logging;
using Correntra.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Correntra.Integration.Tests;

public sealed class InfrastructureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "correntra-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DatabaseInitializesWalSchemaAndSettingsRoundTrip()
    {
        var database = new CorrentraDatabase(Path.Combine(_root, "state", "correntra.db"));
        await database.InitializeAsync();
        var store = new JsonSettingsStore(database);
        var expected = new SampleSetting("tr", true, 4);

        await store.SaveAsync("ui.preferences", expected);
        SampleSetting? actual = await store.LoadAsync<SampleSetting>("ui.preferences");

        Assert.Equal(expected, actual);
        await using SqliteConnection connection = await database.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('jobs','settings','categories','queues','credentials');";
        Assert.Equal(5L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync(), ignoreCase: true);
    }

    [Fact]
    public async Task LengthPrefixedProtocolRoundTripsAndRejectsOversizeFrame()
    {
        var protocol = new LengthPrefixedJsonProtocol();
        await using var stream = new MemoryStream();
        var expected = new SampleSetting("en", false, 8);

        await protocol.WriteAsync(stream, expected);
        stream.Position = 0;
        SampleSetting? actual = await protocol.ReadAsync<SampleSetting>(stream);

        Assert.Equal(expected, actual);

        var invalidPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(invalidPrefix, LengthPrefixedJsonProtocol.MaximumMessageBytes + 1u);
        await using var invalid = new MemoryStream(invalidPrefix);
        invalid.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => protocol.ReadAsync<JsonElement>(invalid));
    }

    [Fact]
    public async Task LocalLogWriterRedactsSecretsAndEscapesNewLines()
    {
        string logs = Path.Combine(_root, "logs");
        await using (var writer = new LocalLogWriter(logs))
        {
            await writer.WriteAsync(
                "INFO",
                "Test",
                "Authorization: Bearer secret\nhttps://example.test/a?token=top-secret&safe=1");
        }

        string text = await File.ReadAllTextAsync(Path.Combine(logs, "correntra.log"));
        Assert.DoesNotContain("Bearer secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.Contains("\\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PathsCreateAllRuntimeDirectories()
    {
        CorrentraPaths paths = CorrentraPaths.Create(Path.Combine(_root, "runtime"), true);

        paths.EnsureCreated();

        Assert.True(paths.IsPortable);
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        Assert.True(Directory.Exists(paths.CredentialsDirectory));
    }

    private sealed record SampleSetting(string Language, bool DarkMode, int Connections);
}
