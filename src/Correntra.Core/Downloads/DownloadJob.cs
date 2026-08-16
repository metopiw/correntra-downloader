using Correntra.Core.Internal;
using Correntra.Core.Security;

namespace Correntra.Core.Downloads;

public sealed class DownloadJob
{
    private DownloadJob(
        JobId id,
        int attemptNumber,
        DownloadSource source,
        string fileName,
        string destinationDirectory,
        CategoryId? categoryId,
        QueueId? queueId,
        DownloadPriority priority,
        DownloadJobState state,
        DownloadExecutionIntent executionIntent,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long bytesTransferred,
        long? totalBytes,
        DownloadFailure? failure)
    {
        Id = id;
        AttemptNumber = attemptNumber;
        Source = source;
        FileName = fileName;
        DestinationDirectory = destinationDirectory;
        CategoryId = categoryId;
        QueueId = queueId;
        Priority = priority;
        State = state;
        ExecutionIntent = executionIntent;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        Failure = failure;
    }

    public JobId Id { get; }

    public int AttemptNumber { get; }

    public DownloadSource Source { get; }

    public string FileName { get; }

    public string DestinationDirectory { get; }

    public string TargetPath => SafePath.CombineUnderRoot(DestinationDirectory, FileName);

    public CategoryId? CategoryId { get; }

    public QueueId? QueueId { get; }

    public DownloadPriority Priority { get; }

    public DownloadJobState State { get; }

    public DownloadExecutionIntent ExecutionIntent { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public long BytesTransferred { get; }

    public long? TotalBytes { get; }

    public DownloadFailure? Failure { get; }

    public bool IsTerminal => DownloadJobStateMachine.IsTerminal(State);

    public static DownloadJob Create(
        DownloadSource source,
        string suggestedFileName,
        string destinationDirectory,
        DateTimeOffset createdAtUtc,
        bool startImmediately = true,
        JobId? id = null,
        CategoryId? categoryId = null,
        QueueId? queueId = null,
        DownloadPriority priority = DownloadPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(source);
        DateTimeOffset timestamp = Guard.UtcTimestamp(createdAtUtc, nameof(createdAtUtc));
        JobId jobId = id ?? JobId.Create();
        ValidateId(jobId, nameof(id));
        ValidateOptionalId(categoryId, nameof(categoryId));
        ValidateOptionalId(queueId, nameof(queueId));
        ValidatePriority(priority);

        string fileName = SafePath.SanitizeFileName(suggestedFileName);
        string destination = SafePath.CanonicalizeDirectory(destinationDirectory, nameof(destinationDirectory));
        DownloadExecutionIntent intent = startImmediately
            ? DownloadExecutionIntent.RunWhenPossible
            : DownloadExecutionIntent.Hold;

        return new DownloadJob(
            jobId,
            1,
            source,
            fileName,
            destination,
            categoryId,
            queueId,
            priority,
            DownloadJobState.Pending,
            intent,
            timestamp,
            timestamp,
            0,
            null,
            null);
    }

    public DownloadJob TransitionTo(
        DownloadJobState nextState,
        DateTimeOffset changedAtUtc,
        DownloadFailure? failure = null)
    {
        DownloadJobStateMachine.EnsureTransition(State, nextState);
        DateTimeOffset timestamp = ValidateChangeTimestamp(changedAtUtc);

        if (nextState == DownloadJobState.Failed && failure is null)
        {
            throw new ArgumentNullException(nameof(failure), "A failed job requires failure details.");
        }

        if (nextState != DownloadJobState.Failed && failure is not null)
        {
            throw new ArgumentException("Failure details are only valid for the Failed state.", nameof(failure));
        }

        DownloadExecutionIntent intent = nextState == DownloadJobState.Paused
            ? DownloadExecutionIntent.Hold
            : ExecutionIntent;

        return Copy(
            state: nextState,
            executionIntent: intent,
            updatedAtUtc: timestamp,
            failure: failure,
            replaceFailure: true);
    }

    public DownloadJob ReportProgress(long bytesTransferred, long? totalBytes, DateTimeOffset changedAtUtc)
    {
        if (State is not DownloadJobState.Downloading and not DownloadJobState.Verifying)
        {
            throw new InvalidOperationException("Progress can only be reported for an active download or verification.");
        }

        if (bytesTransferred < BytesTransferred)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesTransferred), "Progress cannot move backwards.");
        }

        if (totalBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        if (totalBytes is { } total && bytesTransferred > total)
        {
            throw new ArgumentException("Transferred bytes cannot exceed the total size.", nameof(bytesTransferred));
        }

        if (TotalBytes is { } existingTotal && totalBytes is { } newTotal && existingTotal != newTotal)
        {
            throw new InvalidOperationException("The known total size cannot change during an attempt.");
        }

        return Copy(
            bytesTransferred: bytesTransferred,
            totalBytes: totalBytes ?? TotalBytes,
            replaceTotalBytes: true,
            updatedAtUtc: ValidateChangeTimestamp(changedAtUtc));
    }

    public DownloadJob SetExecutionIntent(DownloadExecutionIntent intent, DateTimeOffset changedAtUtc)
    {
        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal job has no mutable execution intent.");
        }

        return Copy(executionIntent: intent, updatedAtUtc: ValidateChangeTimestamp(changedAtUtc));
    }

    public DownloadJob AssignCategory(CategoryId? categoryId, DateTimeOffset changedAtUtc)
    {
        ValidateOptionalId(categoryId, nameof(categoryId));
        EnsureMetadataCanChange();
        return Copy(categoryId: categoryId, replaceCategoryId: true, updatedAtUtc: ValidateChangeTimestamp(changedAtUtc));
    }

    public DownloadJob AssignQueue(QueueId? queueId, DateTimeOffset changedAtUtc)
    {
        ValidateOptionalId(queueId, nameof(queueId));
        EnsureMetadataCanChange();
        return Copy(queueId: queueId, replaceQueueId: true, updatedAtUtc: ValidateChangeTimestamp(changedAtUtc));
    }

    public DownloadJob Rename(string suggestedFileName, DateTimeOffset changedAtUtc)
    {
        EnsureMetadataCanChange();
        string safeName = SafePath.SanitizeFileName(suggestedFileName);
        return Copy(fileName: safeName, updatedAtUtc: ValidateChangeTimestamp(changedAtUtc));
    }

    public DownloadJob RecoverAfterInterruption(DateTimeOffset recoveredAtUtc)
    {
        DateTimeOffset timestamp = ValidateChangeTimestamp(recoveredAtUtc);
        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal job is not recovered in place.");
        }

        DownloadJobState recoveredState = State switch
        {
            DownloadJobState.Pending => DownloadJobState.Pending,
            DownloadJobState.NeedsInput => DownloadJobState.NeedsInput,
            DownloadJobState.Paused => DownloadJobState.Paused,
            DownloadJobState.Cancelling => DownloadJobState.Cancelled,
            _ when ExecutionIntent == DownloadExecutionIntent.Hold => DownloadJobState.Paused,
            _ => DownloadJobState.Queued,
        };

        return Copy(state: recoveredState, updatedAtUtc: timestamp);
    }

    public DownloadJob CreateRetry(DateTimeOffset createdAtUtc)
    {
        if (State is not DownloadJobState.Failed and not DownloadJobState.Cancelled)
        {
            throw new InvalidOperationException("Only a failed or cancelled job can create a retry attempt.");
        }

        DateTimeOffset timestamp = ValidateChangeTimestamp(createdAtUtc);
        return new DownloadJob(
            Id,
            checked(AttemptNumber + 1),
            Source,
            FileName,
            DestinationDirectory,
            CategoryId,
            QueueId,
            Priority,
            DownloadJobState.Pending,
            DownloadExecutionIntent.RunWhenPossible,
            CreatedAtUtc,
            timestamp,
            0,
            null,
            null);
    }

    private DownloadJob Copy(
        string? fileName = null,
        CategoryId? categoryId = null,
        bool replaceCategoryId = false,
        QueueId? queueId = null,
        bool replaceQueueId = false,
        DownloadJobState? state = null,
        DownloadExecutionIntent? executionIntent = null,
        DateTimeOffset? updatedAtUtc = null,
        long? bytesTransferred = null,
        long? totalBytes = null,
        bool replaceTotalBytes = false,
        DownloadFailure? failure = null,
        bool replaceFailure = false)
    {
        return new DownloadJob(
            Id,
            AttemptNumber,
            Source,
            fileName ?? FileName,
            DestinationDirectory,
            replaceCategoryId ? categoryId : CategoryId,
            replaceQueueId ? queueId : QueueId,
            Priority,
            state ?? State,
            executionIntent ?? ExecutionIntent,
            CreatedAtUtc,
            updatedAtUtc ?? UpdatedAtUtc,
            bytesTransferred ?? BytesTransferred,
            replaceTotalBytes ? totalBytes : TotalBytes,
            replaceFailure ? failure : Failure);
    }

    private DateTimeOffset ValidateChangeTimestamp(DateTimeOffset value)
    {
        DateTimeOffset timestamp = Guard.UtcTimestamp(value, nameof(value));
        if (timestamp < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A job timestamp cannot move backwards.");
        }

        return timestamp;
    }

    private void EnsureMetadataCanChange()
    {
        if (State is DownloadJobState.Downloading
            or DownloadJobState.Verifying
            or DownloadJobState.Finalizing
            or DownloadJobState.Cancelling
            or DownloadJobState.Completed)
        {
            throw new InvalidOperationException("Job routing metadata cannot change in the current state.");
        }
    }

    private static void ValidateId(JobId id, string parameterName)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A job ID cannot be empty.", parameterName);
        }
    }

    private static void ValidateOptionalId(CategoryId? id, string parameterName)
    {
        if (id is { IsEmpty: true })
        {
            throw new ArgumentException("A category ID cannot be empty.", parameterName);
        }
    }

    private static void ValidateOptionalId(QueueId? id, string parameterName)
    {
        if (id is { IsEmpty: true })
        {
            throw new ArgumentException("A queue ID cannot be empty.", parameterName);
        }
    }

    private static void ValidatePriority(DownloadPriority priority)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
    }
}
