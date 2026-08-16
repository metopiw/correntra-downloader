using Correntra.Core.Internal;

namespace Correntra.Core.Ipc;

public enum IpcMessageKind
{
    Command = 0,
    Response = 1,
    Event = 2,
}

public interface IIpcPayload
{
    string Type { get; }
}

public interface IIpcCommand : IIpcPayload;

public interface IIpcResponse : IIpcPayload;

public interface IIpcEvent : IIpcPayload;

public static class IpcProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameLength = 256 * 1024;

    public static void ValidateFrameLength(long length)
    {
        if (length is <= 0 or > MaximumFrameLength)
        {
            throw new InvalidDataException($"IPC frame length must be between 1 and {MaximumFrameLength} bytes.");
        }
    }

    public static IpcMessageKind GetKind(IIpcPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload switch
        {
            IIpcCommand => IpcMessageKind.Command,
            IIpcResponse => IpcMessageKind.Response,
            IIpcEvent => IpcMessageKind.Event,
            _ => throw new ArgumentException("The IPC payload has no recognized message kind.", nameof(payload)),
        };
    }
}

public sealed class IpcEnvelope<TPayload>
    where TPayload : IIpcPayload
{
    public IpcEnvelope(
        int protocolVersion,
        IpcMessageKind kind,
        IpcRequestId requestId,
        DateTimeOffset timestampUtc,
        TPayload payload)
    {
        if (protocolVersion is < 1 or > IpcProtocol.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (requestId.IsEmpty)
        {
            throw new ArgumentException("An IPC request ID cannot be empty.", nameof(requestId));
        }

        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        if (IpcProtocol.GetKind(payload) != kind)
        {
            throw new ArgumentException("The envelope kind does not match its payload.", nameof(kind));
        }

        ProtocolVersion = protocolVersion;
        Kind = kind;
        RequestId = requestId;
        TimestampUtc = Guard.UtcTimestamp(timestampUtc, nameof(timestampUtc));
    }

    public int ProtocolVersion { get; }

    public IpcMessageKind Kind { get; }

    public IpcRequestId RequestId { get; }

    public DateTimeOffset TimestampUtc { get; }

    public TPayload Payload { get; }

}

public static class IpcEnvelope
{
    public static IpcEnvelope<TPayload> Create<TPayload>(
        TPayload payload,
        DateTimeOffset timestampUtc,
        IpcRequestId? requestId = null)
        where TPayload : IIpcPayload
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new IpcEnvelope<TPayload>(
            IpcProtocol.CurrentVersion,
            IpcProtocol.GetKind(payload),
            requestId ?? IpcRequestId.Create(),
            timestampUtc,
            payload);
    }
}
