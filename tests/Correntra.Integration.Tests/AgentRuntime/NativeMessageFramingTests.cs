using System.Buffers.Binary;
using System.Text.Json;
using Correntra.NativeHost.Protocol;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class NativeMessageFramingTests
{
    [Fact]
    public async Task WritesNativeEndianLengthAndJsonPayload()
    {
        await using var stream = new MemoryStream();
        var framing = new NativeMessageFraming();

        await framing.WriteAsync(stream, new { accepted = true });

        byte[] bytes = stream.ToArray();
        int length = BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4))
            : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal(bytes.Length - 4, length);
        using JsonDocument json = JsonDocument.Parse(bytes.AsMemory(4));
        Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task ReadsMessageWhenStreamFragmentsEveryRead()
    {
        byte[] json = "{\"value\":42}"u8.ToArray();
        var frame = new byte[json.Length + 4];
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)json.Length));
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame, checked((uint)json.Length));
        }

        json.CopyTo(frame, 4);
        await using var stream = new OneByteReadStream(frame);

        using JsonDocument? document = await NativeMessageFraming.ReadDocumentAsync(stream);

        Assert.NotNull(document);
        Assert.Equal(42, document.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RejectsOversizedFrameBeforeAllocatingPayload()
    {
        var prefix = new byte[4];
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, NativeMessageFraming.MaximumMessageBytes + 1U);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(prefix, NativeMessageFraming.MaximumMessageBytes + 1U);
        }

        await using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessageFraming.ReadDocumentAsync(stream));
    }

    private sealed class OneByteReadStream : MemoryStream
    {
        public OneByteReadStream(byte[] buffer)
            : base(buffer, writable: false)
        {
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
