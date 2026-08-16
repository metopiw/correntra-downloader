using System.Text.Json;
using Correntra.Core.Ipc;

namespace Correntra.Agent.Runtime;

public sealed record AgentRequestEnvelope(
    int ProtocolVersion,
    string Kind,
    string RequestId,
    DateTimeOffset TimestampUtc,
    JsonElement Payload);

public sealed record AgentResponseEnvelope(
    int ProtocolVersion,
    string Kind,
    string RequestId,
    DateTimeOffset TimestampUtc,
    AgentResponsePayload Payload)
{
    public static AgentResponseEnvelope Accepted(
        string requestId,
        string hostVersion,
        string? jobId = null,
        AgentSnapshot? snapshot = null,
        IReadOnlyList<MediaQualityOption>? mediaQualities = null) =>
        new(1, "response", requestId, DateTimeOffset.UtcNow,
            new AgentResponsePayload(true, null, hostVersion, jobId, snapshot, mediaQualities));

    public static AgentResponseEnvelope Rejected(string requestId, string reason, string hostVersion) =>
        new(1, "response", requestId, DateTimeOffset.UtcNow, new AgentResponsePayload(false, reason, hostVersion));
}

public sealed record AgentResponsePayload(
    bool Accepted,
    string? Reason = null,
    string? HostVersion = null,
    string? JobId = null,
    AgentSnapshot? Snapshot = null,
    IReadOnlyList<MediaQualityOption>? MediaQualities = null);

/// <summary>A single selectable media quality derived from a resolved manifest.</summary>
public sealed record MediaQualityOption(
    string Id,
    string DisplayName,
    string Container,
    int? Height,
    long? Bitrate,
    string? MimeType);

