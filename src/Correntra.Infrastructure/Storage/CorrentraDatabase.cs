using Microsoft.Data.Sqlite;

namespace Correntra.Infrastructure.Storage;

public sealed class CorrentraDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public CorrentraDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, MigrationSql, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "PRAGMA user_version = " + SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
        await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS jobs (
            id TEXT NOT NULL PRIMARY KEY,
            attempt INTEGER NOT NULL,
            source_url TEXT NOT NULL,
            request_method INTEGER NOT NULL,
            file_name TEXT NOT NULL,
            destination_directory TEXT NOT NULL,
            category_id TEXT NULL,
            queue_id TEXT NULL,
            priority INTEGER NOT NULL,
            state INTEGER NOT NULL,
            execution_intent INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            bytes_transferred INTEGER NOT NULL,
            total_bytes INTEGER NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            checkpoint_path TEXT NULL,
            row_version INTEGER NOT NULL DEFAULT 1
        );

        CREATE INDEX IF NOT EXISTS ix_jobs_state_priority_created
            ON jobs(state, priority DESC, created_at_utc);
        CREATE INDEX IF NOT EXISTS ix_jobs_queue_state
            ON jobs(queue_id, state);

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT NOT NULL PRIMARY KEY,
            json_value TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS categories (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            destination_directory TEXT NOT NULL,
            sort_order INTEGER NOT NULL,
            json_rules TEXT NOT NULL,
            is_builtin INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS queues (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            json_configuration TEXT NOT NULL,
            is_running INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS credentials (
            job_id TEXT NOT NULL PRIMARY KEY,
            protected_blob BLOB NOT NULL,
            origin TEXT NOT NULL,
            expires_at_utc TEXT NOT NULL,
            FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS schema_history (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );
        INSERT OR IGNORE INTO schema_history(version, applied_at_utc)
            VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;
}

