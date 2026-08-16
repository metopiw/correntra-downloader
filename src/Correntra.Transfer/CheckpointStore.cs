using System.Text.Json;

namespace Correntra.Transfer;

public sealed record SegmentCheckpoint(long Start, long EndInclusive, long CompletedBytes);

public sealed record TransferCheckpoint(
    int FormatVersion,
    string Source,
    string FinalUri,
    long? ContentLength,
    string? EntityTag,
    DateTimeOffset? LastModified,
    IReadOnlyList<SegmentCheckpoint> Segments,
    DateTimeOffset UpdatedAt);

public interface ITransferCheckpointStore
{
    ValueTask<TransferCheckpoint?> LoadAsync(string path, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(string path, TransferCheckpoint checkpoint, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class JsonTransferCheckpointStore : ITransferCheckpointStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool disposed;

    public async ValueTask<TransferCheckpoint?> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TransferCheckpoint>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(
        string path,
        TransferCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ObjectDisposedException.ThrowIf(disposed, this);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    checkpoint,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            writeGate.Release();
        }
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writeGate.Dispose();
    }
}
