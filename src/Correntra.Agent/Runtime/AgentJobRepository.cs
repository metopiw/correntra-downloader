using System.Globalization;
using System.Text.Json;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Security;
using Correntra.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Correntra.Agent.Runtime;

public sealed class AgentJobRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly CorrentraDatabase _database;
    private readonly IJobPayloadProtector _payloadProtector;

    public AgentJobRepository(CorrentraDatabase database, IJobPayloadProtector payloadProtector)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _payloadProtector = payloadProtector ?? throw new ArgumentNullException(nameof(payloadProtector));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_job_requests (
                job_id TEXT NOT NULL PRIMARY KEY,
                protected_json BLOB NOT NULL,
                expires_at_utc TEXT NULL,
                FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentJobRecord> CreateAsync(
        AgentJobCreation creation,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ValidateSource(creation.Source);
        string fileName = SafePath.SanitizeFileName(creation.FileName);
        string destinationDirectory = SafePath.CanonicalizeDirectory(
            creation.DestinationDirectory,
            nameof(creation.DestinationDirectory));
        var headers = ValidateHeaders(creation.Headers);
        DateTimeOffset timestamp = nowUtc.ToUniversalTime();
        var state = creation.NeedsUserConfirmation
            ? DownloadJobState.NeedsInput
            : creation.StartImmediately ? DownloadJobState.Queued : DownloadJobState.Paused;
        var intent = creation.StartImmediately && !creation.NeedsUserConfirmation
            ? DownloadExecutionIntent.RunWhenPossible
            : DownloadExecutionIntent.Hold;
        var job = new AgentJobRecord(
            JobId.Create(),
            1,
            creation.Source,
            fileName,
            destinationDirectory,
            state,
            intent,
            timestamp,
            timestamp,
            headers: headers,
            categoryId: creation.CategoryId,
            queueId: creation.QueueId,
            priority: creation.Priority,
            requestDetailsExpireAtUtc: creation.RequestDetailsExpireAtUtc);

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertJobAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
        if (headers.Count > 0)
        {
            await UpsertRequestDetailsAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<IReadOnlyList<AgentJobRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var jobs = new List<AgentJobRecord>();
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = CreateSelectCommand(connection);
        command.CommandText += " ORDER BY j.created_at_utc DESC;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task<AgentJobRecord?> GetAsync(JobId id, CancellationToken cancellationToken = default)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A job ID is required.", nameof(id));
        }

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = CreateSelectCommand(connection);
        command.CommandText += " WHERE j.id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    public async Task<AgentJobRecord?> TryClaimNextAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        string? id;
        await using (SqliteCommand expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = """
                UPDATE jobs
                SET state = $needsInput,
                    execution_intent = $hold,
                    updated_at_utc = $now,
                    row_version = row_version + 1
                WHERE state = $queued
                  AND EXISTS (
                      SELECT 1 FROM agent_job_requests AS r
                      WHERE r.job_id = jobs.id
                        AND r.expires_at_utc IS NOT NULL
                        AND r.expires_at_utc <= $now
                  );
                """;
            expire.Parameters.AddWithValue("$needsInput", (int)DownloadJobState.NeedsInput);
            expire.Parameters.AddWithValue("$hold", (int)DownloadExecutionIntent.Hold);
            expire.Parameters.AddWithValue("$queued", (int)DownloadJobState.Queued);
            expire.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
            await expire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id
                FROM jobs
                WHERE state = $queued AND execution_intent = $run
                ORDER BY priority DESC, created_at_utc ASC
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$queued", (int)DownloadJobState.Queued);
            select.Parameters.AddWithValue("$run", (int)DownloadExecutionIntent.RunWhenPossible);
            id = (string?)await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (id is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs
                SET state = $downloading, updated_at_utc = $now, row_version = row_version + 1
                WHERE id = $id AND state = $queued AND execution_intent = $run;
                """;
            update.Parameters.AddWithValue("$downloading", (int)DownloadJobState.Downloading);
            update.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$queued", (int)DownloadJobState.Queued);
            update.Parameters.AddWithValue("$run", (int)DownloadExecutionIntent.RunWhenPossible);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(JobId.Parse(id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ChangeStateAsync(
        JobId id,
        IReadOnlyCollection<DownloadJobState> allowedCurrentStates,
        DownloadJobState state,
        DownloadExecutionIntent executionIntent,
        DateTimeOffset nowUtc,
        string? failureCode = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedCurrentStates);
        if (allowedCurrentStates.Count == 0)
        {
            return false;
        }

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string stateParameters = string.Join(", ", allowedCurrentStates.Select((_, index) => "$current" + index.ToString(CultureInfo.InvariantCulture)));
        command.CommandText = $"""
            UPDATE jobs
            SET state = $state,
                execution_intent = $intent,
                updated_at_utc = $now,
                failure_code = $failureCode,
                failure_message = $failureMessage,
                row_version = row_version + 1
            WHERE id = $id AND state IN ({stateParameters});
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$intent", (int)executionIntent);
        command.Parameters.AddWithValue("$now", FormatTimestamp(nowUtc));
        command.Parameters.AddWithValue("$failureCode", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)failureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString());
        int parameterIndex = 0;
        foreach (DownloadJobState current in allowedCurrentStates)
        {
            command.Parameters.AddWithValue("$current" + parameterIndex.ToString(CultureInfo.InvariantCulture), (int)current);
            parameterIndex++;
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task UpdateProgressAsync(
        JobId id,
        DownloadJobState state,
        long bytesTransferred,
        long? totalBytes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET state = $state,
                bytes_transferred = CASE WHEN $bytes > bytes_transferred THEN $bytes ELSE bytes_transferred END,
                total_bytes = COALESCE($total, total_bytes),
                updated_at_utc = $now,
                row_version = row_version + 1
            WHERE id = $id AND state NOT IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$bytes", bytesTransferred);
        command.Parameters.AddWithValue("$total", (object?)totalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatTimestamp(nowUtc));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$completed", (int)DownloadJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)DownloadJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)DownloadJobState.Cancelled);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(JobId id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET attempt = attempt + 1,
                state = $queued,
                execution_intent = $run,
                bytes_transferred = 0,
                total_bytes = NULL,
                failure_code = NULL,
                failure_message = NULL,
                updated_at_utc = $now,
                row_version = row_version + 1
            WHERE id = $id AND state IN ($failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$queued", (int)DownloadJobState.Queued);
        command.Parameters.AddWithValue("$run", (int)DownloadExecutionIntent.RunWhenPossible);
        command.Parameters.AddWithValue("$now", FormatTimestamp(nowUtc));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$failed", (int)DownloadJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)DownloadJobState.Cancelled);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Re-queues a non-terminal job after a transient failure, bumping the
    /// attempt counter so the automatic retry budget is enforced.
    /// </summary>
    public async Task<bool> RequeueAsync(JobId id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET attempt = attempt + 1,
                state = $queued,
                execution_intent = $run,
                failure_code = NULL,
                failure_message = NULL,
                updated_at_utc = $now,
                row_version = row_version + 1
            WHERE id = $id AND state NOT IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$queued", (int)DownloadJobState.Queued);
        command.Parameters.AddWithValue("$run", (int)DownloadExecutionIntent.RunWhenPossible);
        command.Parameters.AddWithValue("$now", FormatTimestamp(nowUtc));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$completed", (int)DownloadJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)DownloadJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)DownloadJobState.Cancelled);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> UpdateFileNameAsync(JobId id, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET file_name = $name,
                updated_at_utc = $now,
                row_version = row_version + 1
            WHERE id = $id AND state NOT IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$completed", (int)DownloadJobState.Completed);
        command.Parameters.AddWithValue("$failed", (int)DownloadJobState.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)DownloadJobState.Cancelled);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> RecoverInterruptedAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        int changed = 0;
        await using (SqliteCommand cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE jobs
                SET state = $cancelled, execution_intent = $hold, updated_at_utc = $now, row_version = row_version + 1
                WHERE state = $cancelling;
                """;
            cancel.Parameters.AddWithValue("$cancelled", (int)DownloadJobState.Cancelled);
            cancel.Parameters.AddWithValue("$hold", (int)DownloadExecutionIntent.Hold);
            cancel.Parameters.AddWithValue("$now", FormatTimestamp(nowUtc));
            cancel.Parameters.AddWithValue("$cancelling", (int)DownloadJobState.Cancelling);
            changed += await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand recover = connection.CreateCommand())
        {
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE jobs
                SET state = CASE WHEN execution_intent = $hold THEN $paused ELSE $queued END,
                    updated_at_utc = $now,
                    row_version = row_version + 1
                WHERE state IN ($probing, $downloading, $verifying, $finalizing);
                """;
            recover.Parameters.AddWithValue("$hold", (int)DownloadExecutionIntent.Hold);
            recover.Parameters.AddWithValue("$paused", (int)DownloadJobState.Paused);
            recover.Parameters.AddWithValue("$queued", (int)DownloadJobState.Queued);
            recover.Parameters.AddWithValue("$now", FormatTimestamp(nowUtc));
            recover.Parameters.AddWithValue("$probing", (int)DownloadJobState.Probing);
            recover.Parameters.AddWithValue("$downloading", (int)DownloadJobState.Downloading);
            recover.Parameters.AddWithValue("$verifying", (int)DownloadJobState.Verifying);
            recover.Parameters.AddWithValue("$finalizing", (int)DownloadJobState.Finalizing);
            changed += await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static async Task InsertJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentJobRecord job,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs(
                id, attempt, source_url, request_method, file_name, destination_directory,
                category_id, queue_id, priority, state, execution_intent, created_at_utc,
                updated_at_utc, bytes_transferred, total_bytes, failure_code, failure_message,
                checkpoint_path, row_version)
            VALUES(
                $id, $attempt, $source, $method, $fileName, $destination,
                $category, $queue, $priority, $state, $intent, $created,
                $updated, $bytes, $total, $failureCode, $failureMessage, NULL, 1);
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$attempt", job.AttemptNumber);
        command.Parameters.AddWithValue("$source", job.Source.AbsoluteUri);
        command.Parameters.AddWithValue("$method", (int)DownloadRequestMethod.Get);
        command.Parameters.AddWithValue("$fileName", job.FileName);
        command.Parameters.AddWithValue("$destination", job.DestinationDirectory);
        command.Parameters.AddWithValue("$category", (object?)job.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$queue", (object?)job.QueueId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", (int)job.Priority);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$intent", (int)job.ExecutionIntent);
        command.Parameters.AddWithValue("$created", FormatTimestamp(job.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(job.UpdatedAtUtc));
        command.Parameters.AddWithValue("$bytes", job.BytesTransferred);
        command.Parameters.AddWithValue("$total", (object?)job.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureCode", (object?)job.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)job.FailureMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertRequestDetailsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentJobRecord job,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new RequestDetails(job.Headers), SerializerOptions);
        byte[] protectedJson = _payloadProtector.Protect(json);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_job_requests(job_id, protected_json, expires_at_utc)
            VALUES($id, $json, $expiry)
            ON CONFLICT(job_id) DO UPDATE SET protected_json = excluded.protected_json, expires_at_utc = excluded.expires_at_utc;
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$json", protectedJson);
        command.Parameters.AddWithValue("$expiry", (object?)job.RequestDetailsExpireAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateSelectCommand(SqliteConnection connection)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.id, j.attempt, j.source_url, j.file_name, j.destination_directory,
                   j.state, j.execution_intent, j.created_at_utc, j.updated_at_utc,
                   j.bytes_transferred, j.total_bytes, j.category_id, j.queue_id,
                   j.priority, j.failure_code, j.failure_message,
                   r.protected_json, r.expires_at_utc
            FROM jobs AS j
            LEFT JOIN agent_job_requests AS r ON r.job_id = j.id
            """;
        return command;
    }

    private AgentJobRecord ReadJob(SqliteDataReader reader)
    {
        IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>();
        DateTimeOffset? expiry = reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17));
        bool detailsExpired = expiry is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;
        if (!reader.IsDBNull(16) && !detailsExpired)
        {
            byte[] protectedJson = (byte[])reader.GetValue(16);
            byte[] json = _payloadProtector.Unprotect(protectedJson);
            RequestDetails? details = JsonSerializer.Deserialize<RequestDetails>(json, SerializerOptions);
            headers = ValidateHeaders(details?.Headers);
        }

        return new AgentJobRecord(
            JobId.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            new Uri(reader.GetString(2), UriKind.Absolute),
            reader.GetString(3),
            reader.GetString(4),
            (DownloadJobState)reader.GetInt32(5),
            (DownloadExecutionIntent)reader.GetInt32(6),
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            headers,
            reader.IsDBNull(11) ? null : new CategoryId(Guid.ParseExact(reader.GetString(11), "N")),
            reader.IsDBNull(12) ? null : new QueueId(Guid.ParseExact(reader.GetString(12), "N")),
            (DownloadPriority)reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            expiry);
    }

    private static Dictionary<string, string> ValidateHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        _ = new HttpHeaderSet(headers);
        foreach ((string name, string value) in headers)
        {
            if (name is "Host" or "Content-Length" or "Transfer-Encoding" or "Connection")
            {
                continue;
            }

            result.Add(name, value);
        }

        return result;
    }

    private static void ValidateSource(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri ||
            (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(source.UserInfo))
        {
            throw new ArgumentException("Only credential-free absolute HTTP and HTTPS URLs are supported.", nameof(source));
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record RequestDetails(IReadOnlyDictionary<string, string> Headers);
}
