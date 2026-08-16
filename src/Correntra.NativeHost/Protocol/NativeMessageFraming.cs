using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Correntra.NativeHost.Protocol;

public sealed class NativeMessageFraming
{
    public const int MaximumMessageBytes = 256 * 1024;
    private readonly JsonSerializerOptions _serializerOptions;

    public NativeMessageFraming(JsonSerializerOptions? serializerOptions = null)
    {
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public static async Task<JsonDocument?> ReadDocumentAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(uint)];
        int prefixLength = await ReadExactlyOrEofAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixLength == 0)
        {
            return null;
        }

        if (prefixLength != prefix.Length)
        {
            throw new EndOfStreamException("The native message length prefix was truncated.");
        }

        uint length = BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(prefix)
            : BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native messages must be 1-{MaximumMessageBytes} bytes.");
        }

        var payload = new byte[length];
        int payloadLength = await ReadExactlyOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadLength != payload.Length)
        {
            throw new EndOfStreamException("The native message payload was truncated.");
        }

        return JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
    }

    public async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
        if (payload.Length is 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native messages must be 1-{MaximumMessageBytes} bytes.");
        }

        var prefix = new byte[sizeof(uint)];
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)payload.Length));
        }

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
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
