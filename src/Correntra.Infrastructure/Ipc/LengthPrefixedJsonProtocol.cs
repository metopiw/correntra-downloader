using System.Buffers.Binary;
using System.Text.Json;

namespace Correntra.Infrastructure.Ipc;

public sealed class LengthPrefixedJsonProtocol
{
    public const int MaximumMessageBytes = 256 * 1024;
    private readonly JsonSerializerOptions _serializerOptions;

    public LengthPrefixedJsonProtocol(JsonSerializerOptions? serializerOptions = null)
    {
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
        if (payload.Length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException($"IPC message size must be 1-{MaximumMessageBytes} bytes.");
        }

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(uint)];
        int prefixRead = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0)
        {
            return default;
        }

        if (prefixRead != prefix.Length)
        {
            throw new EndOfStreamException("IPC length prefix was truncated.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException($"IPC message size must be 1-{MaximumMessageBytes} bytes.");
        }

        var payload = new byte[length];
        int payloadRead = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead != payload.Length)
        {
            throw new EndOfStreamException("IPC message payload was truncated.");
        }

        return JsonSerializer.Deserialize<T>(payload, _serializerOptions);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

