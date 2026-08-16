using System.Collections.ObjectModel;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;

namespace Correntra.Agent.Runtime;

public sealed record AgentJobRecord
{
    public AgentJobRecord(
        JobId id,
        int attemptNumber,
        Uri source,
        string fileName,
        string destinationDirectory,
        DownloadJobState state,
        DownloadExecutionIntent executionIntent,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long bytesTransferred = 0,
        long? totalBytes = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CategoryId? categoryId = null,
        QueueId? queueId = null,
        DownloadPriority priority = DownloadPriority.Normal,
        string? failureCode = null,
        string? failureMessage = null,
        DateTimeOffset? requestDetailsExpireAtUtc = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A job ID is required.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS sources are supported.", nameof(source));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (!Enum.IsDefined(executionIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(executionIntent));
        }

        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        if (bytesTransferred < 0 || totalBytes is < 0 || (totalBytes is { } total && bytesTransferred > total))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesTransferred));
        }

        Id = id;
        AttemptNumber = attemptNumber;
        Source = source;
        FileName = fileName;
        DestinationDirectory = Path.GetFullPath(destinationDirectory);
        State = state;
        ExecutionIntent = executionIntent;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
        CategoryId = categoryId;
        QueueId = queueId;
        Priority = priority;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
        RequestDetailsExpireAtUtc = requestDetailsExpireAtUtc?.ToUniversalTime();
    }

    public JobId Id { get; }

    public int AttemptNumber { get; }

    public Uri Source { get; }

    public string FileName { get; }

    public string DestinationDirectory { get; }

    public string DestinationPath => Path.Combine(DestinationDirectory, FileName);

    public DownloadJobState State { get; }

    public DownloadExecutionIntent ExecutionIntent { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public long BytesTransferred { get; }

    public long? TotalBytes { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public CategoryId? CategoryId { get; }

    public QueueId? QueueId { get; }

    public DownloadPriority Priority { get; }

    public string? FailureCode { get; }

    public string? FailureMessage { get; }

    public DateTimeOffset? RequestDetailsExpireAtUtc { get; }

    public DownloadJobSnapshot ToSnapshot() => new(
        Id,
        AttemptNumber,
        FileName,
        DestinationDirectory,
        Source.GetLeftPart(UriPartial.Path),
        State,
        BytesTransferred,
        TotalBytes,
        UpdatedAtUtc,
        CategoryId,
        QueueId,
        FailureCode);
}

public sealed record AgentJobCreation(
    Uri Source,
    string FileName,
    string DestinationDirectory,
    bool StartImmediately,
    bool NeedsUserConfirmation,
    IReadOnlyDictionary<string, string>? Headers = null,
    DateTimeOffset? RequestDetailsExpireAtUtc = null,
    CategoryId? CategoryId = null,
    QueueId? QueueId = null,
    DownloadPriority Priority = DownloadPriority.Normal);

