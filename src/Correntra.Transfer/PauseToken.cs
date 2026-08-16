namespace Correntra.Transfer;

public sealed class PauseController
{
    private readonly object sync = new();
    private TaskCompletionSource<bool> resumed = CreateCompletedSource();

    public bool IsPaused
    {
        get
        {
            lock (sync)
            {
                return !resumed.Task.IsCompleted;
            }
        }
    }

    public PauseToken Token => new(this);

    public void Pause()
    {
        lock (sync)
        {
            if (resumed.Task.IsCompleted)
            {
                resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool> source;
        lock (sync)
        {
            source = resumed;
        }

        source.TrySetResult(true);
    }

    internal Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (sync)
        {
            task = resumed.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }
}

public readonly struct PauseToken
{
    private readonly PauseController? controller;

    internal PauseToken(PauseController controller) => this.controller = controller;

    public bool IsPaused => controller?.IsPaused ?? false;

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default) =>
        controller?.WaitWhilePausedAsync(cancellationToken) ?? Task.CompletedTask;
}
