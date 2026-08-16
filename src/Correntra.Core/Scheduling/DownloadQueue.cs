using System.Collections.Immutable;
using Correntra.Core.Internal;

namespace Correntra.Core.Scheduling;

public enum QueueCompletionAction
{
    None = 0,
    Sleep = 1,
    Hibernate = 2,
    ShutDown = 3,
}

public enum DownloadQueueState
{
    Stopped = 0,
    WaitingForSchedule = 1,
    Running = 2,
    Stopping = 3,
    Completed = 4,
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This type is a domain queue aggregate, not a collection implementation.")]
public sealed class DownloadQueue
{
    public const int MaximumConcurrency = 32;
    public static readonly TimeSpan MaximumCompletionCountdown = TimeSpan.FromMinutes(10);

    public DownloadQueue(
        QueueId id,
        string name,
        int maxConcurrentDownloads,
        long? speedLimitBytesPerSecond = null,
        QueueSchedule? schedule = null,
        QueueCompletionAction completionAction = QueueCompletionAction.None,
        TimeSpan? completionCountdown = null,
        IEnumerable<JobId>? jobIds = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(id));
        }

        if (maxConcurrentDownloads is < 1 or > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentDownloads));
        }

        if (speedLimitBytesPerSecond is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedLimitBytesPerSecond));
        }

        if (!Enum.IsDefined(completionAction))
        {
            throw new ArgumentOutOfRangeException(nameof(completionAction));
        }

        TimeSpan countdown = completionCountdown ?? TimeSpan.FromSeconds(30);
        if (countdown < TimeSpan.Zero || countdown > MaximumCompletionCountdown)
        {
            throw new ArgumentOutOfRangeException(nameof(completionCountdown));
        }

        ImmutableArray<JobId> jobs = (jobIds ?? []).ToImmutableArray();
        if (jobs.Any(static jobId => jobId.IsEmpty))
        {
            throw new ArgumentException("A queue cannot contain an empty job ID.", nameof(jobIds));
        }

        if (jobs.Distinct().Count() != jobs.Length)
        {
            throw new ArgumentException("A queue cannot contain the same job more than once.", nameof(jobIds));
        }

        Id = id;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 80);
        MaxConcurrentDownloads = maxConcurrentDownloads;
        SpeedLimitBytesPerSecond = speedLimitBytesPerSecond;
        Schedule = schedule;
        CompletionAction = completionAction;
        CompletionCountdown = countdown;
        JobIds = jobs;
    }

    public QueueId Id { get; }

    public string Name { get; }

    public int MaxConcurrentDownloads { get; }

    public long? SpeedLimitBytesPerSecond { get; }

    public QueueSchedule? Schedule { get; }

    public QueueCompletionAction CompletionAction { get; }

    public TimeSpan CompletionCountdown { get; }

    public ImmutableArray<JobId> JobIds { get; }

    public DownloadQueue Enqueue(JobId jobId)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(jobId));
        }

        if (JobIds.Contains(jobId))
        {
            return this;
        }

        return Copy(JobIds.Add(jobId));
    }

    public DownloadQueue Remove(JobId jobId)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(jobId));
        }

        int index = JobIds.IndexOf(jobId);
        return index < 0 ? this : Copy(JobIds.RemoveAt(index));
    }

    public DownloadQueue Move(JobId jobId, int newIndex)
    {
        int currentIndex = JobIds.IndexOf(jobId);
        if (currentIndex < 0)
        {
            throw new ArgumentException("The job is not in this queue.", nameof(jobId));
        }

        if (newIndex < 0 || newIndex >= JobIds.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        }

        if (currentIndex == newIndex)
        {
            return this;
        }

        ImmutableArray<JobId> reordered = JobIds.RemoveAt(currentIndex).Insert(newIndex, jobId);
        return Copy(reordered);
    }

    private DownloadQueue Copy(ImmutableArray<JobId> jobs)
    {
        return new DownloadQueue(
            Id,
            Name,
            MaxConcurrentDownloads,
            SpeedLimitBytesPerSecond,
            Schedule,
            CompletionAction,
            CompletionCountdown,
            jobs);
    }
}
