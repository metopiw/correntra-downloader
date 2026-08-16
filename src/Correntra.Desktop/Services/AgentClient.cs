using System.IO.Pipes;
using System.Text.Json;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Core.Scheduling;
using Correntra.Infrastructure.Ipc;

namespace Correntra.Desktop.Services;

public sealed record AgentCommandResult(
    bool Accepted,
    string? Reason,
    string? HostVersion,
    string? JobId,
    AgentSnapshot? Snapshot,
    IReadOnlyList<MediaQualityOption>? MediaQualities = null);

public sealed record MediaQualityOption(
    string Id,
    string DisplayName,
    string Container,
    int? Height,
    long? Bitrate,
    string? MimeType);

public sealed class AgentClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly LengthPrefixedJsonProtocol protocol = new(SerializerOptions);
    private readonly string pipeName;

    public AgentClient(string? pipeName = null)
    {
        this.pipeName = pipeName ?? AgentPipeNames.ForCurrentUser();
    }

    public async Task<AgentCommandResult> PingAsync(CancellationToken cancellationToken = default) =>
        await SendCoreAsync(
            "host.ping",
            new { },
            TimeSpan.FromSeconds(1),
            cancellationToken).ConfigureAwait(false);

    public async Task<AgentCommandResult> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        await SendAsync("agent.snapshot.get", new { }, cancellationToken).ConfigureAwait(false);

    public async Task<AgentCommandResult> CreateDownloadAsync(
        string url,
        string fileName,
        string destinationDirectory,
        bool startImmediately,
        CancellationToken cancellationToken = default) =>
        await SendAsync(
            "download.create",
            new { url, fileName, destinationDirectory, startImmediately, headers = new { } },
            cancellationToken).ConfigureAwait(false);

    public async Task<AgentCommandResult> ChangeJobAsync(
        string command,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return await SendAsync(command, new { jobId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCommandResult> RemoveDownloadAsync(
        string jobId,
        bool deleteDownloadedFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return await SendAsync(
            "download.remove",
            new { jobId, deleteDownloadedFile },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCommandResult> ConfirmDownloadAsync(
        string jobId,
        bool startImmediately,
        CancellationToken cancellationToken = default) =>
        await SendAsync("download.confirm", new { jobId, startImmediately }, cancellationToken).ConfigureAwait(false);

    public async Task<AgentCommandResult> ResolveMediaAsync(
        string url,
        string? candidateId,
        string? title,
        string? referrer,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default) =>
        await SendAsync(
            "media.resolve",
            new { url, candidateId, title, referrer, headers },
            cancellationToken).ConfigureAwait(false);

    public async Task<AgentCommandResult> SendAsync(
        string kind,
        object payload,
        CancellationToken cancellationToken = default) =>
        await SendCoreAsync(kind, payload, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

    private async Task<AgentCommandResult> SendCoreAsync(
        string kind,
        object payload,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);
        string requestId = "r_" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        using var timeout = new CancellationTokenSource(timeoutDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            await protocol.WriteAsync(
                pipe,
                new AgentClientRequest(1, kind, requestId, DateTimeOffset.UtcNow, payload),
                linked.Token).ConfigureAwait(false);
            AgentClientResponse? response = await protocol.ReadAsync<AgentClientResponse>(pipe, linked.Token)
                .ConfigureAwait(false);
            if (response is null ||
                response.Payload is null ||
                response.ProtocolVersion != 1 ||
                response.TimestampUtc.Offset != TimeSpan.Zero ||
                !string.Equals(response.Kind, "response", StringComparison.Ordinal) ||
                !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The background Agent returned an invalid response.");
            }

            return new AgentCommandResult(
                response.Payload.Accepted,
                response.Payload.Reason,
                response.Payload.HostVersion,
                response.Payload.JobId,
                response.Payload.Snapshot?.ToDomain(),
                response.Payload.MediaQualities?
                    .Select(static quality => new MediaQualityOption(
                        quality.Id,
                        quality.DisplayName,
                        quality.Container,
                        quality.Height,
                        quality.Bitrate,
                        quality.MimeType))
                    .ToList());
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The background Agent did not respond in time.", exception);
        }
    }

    private sealed record AgentClientRequest(
        int ProtocolVersion,
        string Kind,
        string RequestId,
        DateTimeOffset TimestampUtc,
        object Payload);

    private sealed record AgentClientResponse(
        int ProtocolVersion,
        string Kind,
        string RequestId,
        DateTimeOffset TimestampUtc,
        AgentClientResponsePayload? Payload);

    private sealed record AgentClientResponsePayload(
        bool Accepted,
        string? Reason,
        string? HostVersion,
        string? JobId,
        AgentSnapshotWire? Snapshot,
        MediaQualityWire[]? MediaQualities);

    private sealed class AgentSnapshotWire
    {
        public DateTimeOffset GeneratedAtUtc { get; init; }

        public DownloadJobSnapshotWire[] Jobs { get; init; } = [];

        public QueueSnapshotWire[] Queues { get; init; } = [];

        public long AggregateBytesPerSecond { get; init; }

        public AgentSnapshot ToDomain() => new(
            GeneratedAtUtc,
            Jobs.Select(static job => job.ToDomain()),
            Queues.Select(static queue => queue.ToDomain()),
            AggregateBytesPerSecond);
    }

    private sealed class DownloadJobSnapshotWire
    {
        public IdentifierWire Id { get; init; } = new();

        public int AttemptNumber { get; init; }

        public string FileName { get; init; } = string.Empty;

        public string DestinationDirectory { get; init; } = string.Empty;

        public string SourceDisplayUri { get; init; } = string.Empty;

        public DownloadJobState State { get; init; }

        public long BytesTransferred { get; init; }

        public long? TotalBytes { get; init; }

        public DateTimeOffset UpdatedAtUtc { get; init; }

        public IdentifierWire? CategoryId { get; init; }

        public IdentifierWire? QueueId { get; init; }

        public string? FailureCode { get; init; }

        public DownloadJobSnapshot ToDomain() => new(
            new JobId(Id.Value),
            AttemptNumber,
            FileName,
            DestinationDirectory,
            SourceDisplayUri,
            State,
            BytesTransferred,
            TotalBytes,
            UpdatedAtUtc,
            CategoryId is null ? null : new CategoryId(CategoryId.Value),
            QueueId is null ? null : new QueueId(QueueId.Value),
            FailureCode);
    }

    private sealed class QueueSnapshotWire
    {
        public IdentifierWire Id { get; init; } = new();

        public string Name { get; init; } = string.Empty;

        public DownloadQueueState State { get; init; }

        public int ActiveDownloads { get; init; }

        public int PendingDownloads { get; init; }

        public long BytesPerSecond { get; init; }

        public DateTimeOffset? CompletionActionAtUtc { get; init; }

        public QueueSnapshot ToDomain() => new(
            new QueueId(Id.Value),
            Name,
            State,
            ActiveDownloads,
            PendingDownloads,
            BytesPerSecond,
            CompletionActionAtUtc);
    }

    private sealed class IdentifierWire
    {
        public Guid Value { get; init; }
    }

    private sealed class MediaQualityWire
    {
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Container { get; init; } = string.Empty;

        public int? Height { get; init; }

        public long? Bitrate { get; init; }

        public string? MimeType { get; init; }
    }
}
