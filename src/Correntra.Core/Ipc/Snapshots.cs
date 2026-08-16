using System.Collections.Immutable;
using Correntra.Core.Downloads;
using Correntra.Core.Internal;
using Correntra.Core.Scheduling;
using Correntra.Core.Security;

namespace Correntra.Core.Ipc;

public sealed record DownloadJobSnapshot
{
    public DownloadJobSnapshot(
        JobId id,
        int attemptNumber,
        string fileName,
        string destinationDirectory,
        string sourceDisplayUri,
        DownloadJobState state,
        long bytesTransferred,
        long? totalBytes,
        DateTimeOffset updatedAtUtc,
        CategoryId? categoryId = null,
        QueueId? queueId = null,
        string? failureCode = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (bytesTransferred < 0 || totalBytes is < 0 || (totalBytes is { } total && bytesTransferred > total))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesTransferred));
        }

        Id = id;
        AttemptNumber = attemptNumber;
        FileName = SafePath.ValidateComponent(fileName, nameof(fileName));
        DestinationDirectory = SafePath.CanonicalizeDirectory(destinationDirectory, nameof(destinationDirectory));
        SourceDisplayUri = Guard.NotNullOrWhiteSpace(sourceDisplayUri, nameof(sourceDisplayUri), 4_096);
        State = state;
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        UpdatedAtUtc = Guard.UtcTimestamp(updatedAtUtc, nameof(updatedAtUtc));
        CategoryId = categoryId;
        QueueId = queueId;
        FailureCode = failureCode is null ? null : Guard.NotNullOrWhiteSpace(failureCode, nameof(failureCode), 80);
    }

    public JobId Id { get; }

    public int AttemptNumber { get; }

    public string FileName { get; }

    public string DestinationDirectory { get; }

    public string SourceDisplayUri { get; }

    public DownloadJobState State { get; }

    public long BytesTransferred { get; }

    public long? TotalBytes { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public CategoryId? CategoryId { get; }

    public QueueId? QueueId { get; }

    public string? FailureCode { get; }

    public static DownloadJobSnapshot FromJob(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new DownloadJobSnapshot(
            job.Id,
            job.AttemptNumber,
            job.FileName,
            job.DestinationDirectory,
            SensitiveDataRedactor.RedactUri(job.Source.Url),
            job.State,
            job.BytesTransferred,
            job.TotalBytes,
            job.UpdatedAtUtc,
            job.CategoryId,
            job.QueueId,
            job.Failure?.Code);
    }
}

public sealed record QueueSnapshot
{
    public QueueSnapshot(
        QueueId id,
        string name,
        DownloadQueueState state,
        int activeDownloads,
        int pendingDownloads,
        long bytesPerSecond,
        DateTimeOffset? completionActionAtUtc = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(id));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (activeDownloads < 0 || pendingDownloads < 0 || bytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeDownloads));
        }

        Id = id;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 80);
        State = state;
        ActiveDownloads = activeDownloads;
        PendingDownloads = pendingDownloads;
        BytesPerSecond = bytesPerSecond;
        CompletionActionAtUtc = completionActionAtUtc is null
            ? null
            : Guard.UtcTimestamp(completionActionAtUtc.Value, nameof(completionActionAtUtc));
    }

    public QueueId Id { get; }

    public string Name { get; }

    public DownloadQueueState State { get; }

    public int ActiveDownloads { get; }

    public int PendingDownloads { get; }

    public long BytesPerSecond { get; }

    public DateTimeOffset? CompletionActionAtUtc { get; }
}

public sealed record AgentSnapshot : IIpcResponse
{
    public AgentSnapshot(
        DateTimeOffset generatedAtUtc,
        IEnumerable<DownloadJobSnapshot> jobs,
        IEnumerable<QueueSnapshot>? queues = null,
        long aggregateBytesPerSecond = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(aggregateBytesPerSecond);

        GeneratedAtUtc = Guard.UtcTimestamp(generatedAtUtc, nameof(generatedAtUtc));
        Jobs = jobs?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(jobs));
        Queues = (queues ?? []).ToImmutableArray();
        if (Jobs.Any(static job => job is null) || Queues.Any(static queue => queue is null))
        {
            throw new ArgumentException("Snapshots cannot contain null entries.");
        }

        AggregateBytesPerSecond = aggregateBytesPerSecond;
    }

    public string Type => "agent.snapshot";

    public DateTimeOffset GeneratedAtUtc { get; }

    public ImmutableArray<DownloadJobSnapshot> Jobs { get; }

    public ImmutableArray<QueueSnapshot> Queues { get; }

    public long AggregateBytesPerSecond { get; }
}

public sealed record JobChangedEvent : IIpcEvent
{
    public JobChangedEvent(DownloadJobSnapshot job)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
    }

    public string Type => "download.changed";

    public DownloadJobSnapshot Job { get; }
}
