using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Correntra.Infrastructure.Storage;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private readonly CorrentraDatabase _database;

    public JsonSettingsStore(CorrentraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        string json = JsonSerializer.Serialize(value, SerializerOptions);
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, json_value, updated_at_utc)
            VALUES($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET
                json_value=excluded.json_value,
                updated_at_utc=excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", json);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT json_value FROM settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json ? JsonSerializer.Deserialize<T>(json, SerializerOptions) : default;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 100 || key.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("The settings key is invalid.", nameof(key));
        }
    }
}

