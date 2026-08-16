using System.Text.Json;

namespace Correntra.NativeHost.Protocol;

public sealed record NativeRequestEnvelope(
    int ProtocolVersion,
    string Kind,
    string RequestId,
    DateTimeOffset TimestampUtc,
    JsonElement Payload);

public sealed record NativeResponseEnvelope(
    int ProtocolVersion,
    string Kind,
    string RequestId,
    DateTimeOffset TimestampUtc,
    NativeResponsePayload Payload)
{
    public static NativeResponseEnvelope Create(
        string requestId,
        bool accepted,
        string hostVersion,
        string? reason = null,
        string? jobId = null,
        IReadOnlyList<NativeMediaQuality>? mediaQualities = null) =>
        new(
            1,
            "response",
            requestId,
            DateTimeOffset.UtcNow,
            new NativeResponsePayload(accepted, reason, hostVersion, jobId, mediaQualities));
}

public sealed record NativeResponsePayload(
    bool Accepted,
    string? Reason,
    string HostVersion,
    string? JobId = null,
    IReadOnlyList<NativeMediaQuality>? MediaQualities = null);

/// <summary>Mirrors the agent's MediaQualityOption wire shape (camelCase).</summary>
public sealed record NativeMediaQuality(
    string Id,
    string DisplayName,
    string Container,
    int? Height = null,
    long? Bitrate = null,
    string? MimeType = null);

