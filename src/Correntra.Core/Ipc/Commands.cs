using Correntra.Core.Browser;
using Correntra.Core.Downloads;
using Correntra.Core.Internal;
using Correntra.Core.Media;
using Correntra.Core.Settings;

namespace Correntra.Core.Ipc;

public sealed record PingCommand : IIpcCommand
{
    public string Type => "ping";
}

public sealed record GetAgentSnapshotCommand : IIpcCommand
{
    public string Type => "agent.snapshot.get";
}

public sealed record SubscribeAgentEventsCommand : IIpcCommand
{
    public string Type => "agent.events.subscribe";
}

public sealed record CreateDownloadCommand : IIpcCommand
{
    public CreateDownloadCommand(
        DownloadSource source,
        string suggestedFileName,
        string destinationDirectory,
        bool startImmediately,
        CategoryId? categoryId = null,
        QueueId? queueId = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        SuggestedFileName = Security.SafePath.SanitizeFileName(suggestedFileName);
        DestinationDirectory = Security.SafePath.CanonicalizeDirectory(destinationDirectory, nameof(destinationDirectory));
        if (categoryId is { IsEmpty: true })
        {
            throw new ArgumentException("A category ID cannot be empty.", nameof(categoryId));
        }

        if (queueId is { IsEmpty: true })
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(queueId));
        }

        StartImmediately = startImmediately;
        CategoryId = categoryId;
        QueueId = queueId;
    }

    public string Type => "download.create";

    public DownloadSource Source { get; }

    public string SuggestedFileName { get; }

    public string DestinationDirectory { get; }

    public bool StartImmediately { get; }

    public CategoryId? CategoryId { get; }

    public QueueId? QueueId { get; }
}

public abstract record DownloadJobCommand : IIpcCommand
{
    protected DownloadJobCommand(JobId jobId)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(jobId));
        }

        JobId = jobId;
    }

    public abstract string Type { get; }

    public JobId JobId { get; }
}

public sealed record PauseDownloadCommand : DownloadJobCommand
{
    public PauseDownloadCommand(JobId jobId)
        : base(jobId)
    {
    }

    public override string Type => "download.pause";
}

public sealed record ResumeDownloadCommand : DownloadJobCommand
{
    public ResumeDownloadCommand(JobId jobId)
        : base(jobId)
    {
    }

    public override string Type => "download.resume";
}

public sealed record CancelDownloadCommand : DownloadJobCommand
{
    public CancelDownloadCommand(JobId jobId)
        : base(jobId)
    {
    }

    public override string Type => "download.cancel";
}

public sealed record RetryDownloadCommand : DownloadJobCommand
{
    public RetryDownloadCommand(JobId jobId)
        : base(jobId)
    {
    }

    public override string Type => "download.retry";
}

public sealed record RemoveDownloadCommand : DownloadJobCommand
{
    public RemoveDownloadCommand(JobId jobId, bool deleteDownloadedFile = false)
        : base(jobId)
    {
        DeleteDownloadedFile = deleteDownloadedFile;
    }

    public override string Type => "download.remove";

    public bool DeleteDownloadedFile { get; }
}

public sealed record CaptureBrowserDownloadCommand : IIpcCommand
{
    public CaptureBrowserDownloadCommand(BrowserDownloadCapture capture)
    {
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public string Type => "browser.download.capture";

    public BrowserDownloadCapture Capture { get; }
}

public sealed record SelectMediaCandidateCommand : IIpcCommand
{
    public SelectMediaCandidateCommand(MediaSelectionRequest selection)
    {
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    public string Type => "media.candidate.select";

    public MediaSelectionRequest Selection { get; }
}

public sealed record UpdateSettingsCommand : IIpcCommand
{
    public UpdateSettingsCommand(ApplicationSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Type => "settings.update";

    public ApplicationSettings Settings { get; }
}

public sealed record StartQueueCommand : IIpcCommand
{
    public StartQueueCommand(QueueId queueId)
    {
        if (queueId.IsEmpty)
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(queueId));
        }

        QueueId = queueId;
    }

    public string Type => "queue.start";

    public QueueId QueueId { get; }
}

public sealed record StopQueueCommand : IIpcCommand
{
    public StopQueueCommand(QueueId queueId)
    {
        if (queueId.IsEmpty)
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(queueId));
        }

        QueueId = queueId;
    }

    public string Type => "queue.stop";

    public QueueId QueueId { get; }
}

public sealed record CancelCompletionActionCommand : IIpcCommand
{
    public CancelCompletionActionCommand(QueueId queueId)
    {
        if (queueId.IsEmpty)
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(queueId));
        }

        QueueId = queueId;
    }

    public string Type => "queue.completion-action.cancel";

    public QueueId QueueId { get; }
}

public static class IpcCommandTypes
{
    /// <summary>
    /// Command types accepted over the agent's named-pipe IPC surface. The
    /// former native messaging host and its command allow-list were removed:
    /// the loopback HTTP bridge (pinned extension origin + shared token) is
    /// the only browser integration.
    /// </summary>
    public static bool IsAllowed(string? type)
    {
        return type is "ping"
            or "browser.download.capture"
            or "media.candidate.select";
    }
}
