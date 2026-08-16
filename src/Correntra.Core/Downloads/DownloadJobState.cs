namespace Correntra.Core.Downloads;

public enum DownloadJobState
{
    Pending = 0,
    Probing = 1,
    NeedsInput = 2,
    Queued = 3,
    Downloading = 4,
    Paused = 5,
    Verifying = 6,
    Finalizing = 7,
    Cancelling = 8,
    Completed = 9,
    Failed = 10,
    Cancelled = 11,
}

public enum DownloadExecutionIntent
{
    RunWhenPossible = 0,
    Hold = 1,
}

public enum DownloadPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}

public static class DownloadJobStateMachine
{
    public static bool CanTransition(DownloadJobState current, DownloadJobState next)
    {
        return current switch
        {
            DownloadJobState.Pending => next is DownloadJobState.Probing
                or DownloadJobState.Queued
                or DownloadJobState.Paused
                or DownloadJobState.NeedsInput
                or DownloadJobState.Cancelling
                or DownloadJobState.Cancelled
                or DownloadJobState.Failed,
            DownloadJobState.Probing => next is DownloadJobState.Queued
                or DownloadJobState.Paused
                or DownloadJobState.NeedsInput
                or DownloadJobState.Cancelling
                or DownloadJobState.Failed,
            DownloadJobState.NeedsInput => next is DownloadJobState.Probing
                or DownloadJobState.Queued
                or DownloadJobState.Cancelling
                or DownloadJobState.Cancelled
                or DownloadJobState.Failed,
            DownloadJobState.Queued => next is DownloadJobState.Downloading
                or DownloadJobState.Paused
                or DownloadJobState.NeedsInput
                or DownloadJobState.Cancelling
                or DownloadJobState.Failed,
            DownloadJobState.Downloading => next is DownloadJobState.Paused
                or DownloadJobState.Verifying
                or DownloadJobState.Cancelling
                or DownloadJobState.Failed,
            DownloadJobState.Paused => next is DownloadJobState.Queued
                or DownloadJobState.Cancelling
                or DownloadJobState.Cancelled,
            DownloadJobState.Verifying => next is DownloadJobState.Finalizing
                or DownloadJobState.Cancelling
                or DownloadJobState.Failed,
            DownloadJobState.Finalizing => next is DownloadJobState.Completed
                or DownloadJobState.Cancelling
                or DownloadJobState.Failed,
            DownloadJobState.Cancelling => next is DownloadJobState.Cancelled
                or DownloadJobState.Failed,
            DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled => false,
            _ => false,
        };
    }

    public static void EnsureTransition(DownloadJobState current, DownloadJobState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"A download job cannot transition from {current} to {next}.");
        }
    }

    public static bool IsTerminal(DownloadJobState state) =>
        state is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled;

    public static bool IsTransferActive(DownloadJobState state) =>
        state is DownloadJobState.Probing
            or DownloadJobState.Queued
            or DownloadJobState.Downloading
            or DownloadJobState.Verifying
            or DownloadJobState.Finalizing
            or DownloadJobState.Cancelling;
}
