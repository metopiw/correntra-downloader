using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Avalonia.Threading;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;
using Correntra.Desktop.Views;
using Correntra.Infrastructure.Ipc;

namespace Correntra.Desktop.Services;

/// <summary>
/// Owns the short-lived IPC calls made by the desktop shell. All view-model
/// updates are marshalled to Avalonia's UI dispatcher and all transport work is
/// cancelled before application shutdown, so the UI thread is never blocked on
/// a pipe operation.
/// </summary>
public sealed class DesktopAgentBridge : IAsyncDisposable
{
    private readonly AgentClient client;
    private readonly MainViewModel viewModel;
    private readonly MainWindow window;
    private readonly ConcurrentQueue<string> pendingConfirmations = new();
    private readonly ConcurrentDictionary<string, byte> pendingConfirmationSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object lifecycleSync = new();
    private readonly object actionTasksSync = new();
    private readonly HashSet<Task> actionTasks = [];
    private Task? startupTask;
    private Task? pollingTask;
    private Task? activationServerTask;
    private bool actionHandlerAttached;
    private bool stopRequested;
    private int confirmationShowing;
    private int disposed;

    public DesktopAgentBridge(MainViewModel viewModel, MainWindow window, IEnumerable<string> arguments)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(arguments);
        client = new AgentClient();
        string[] args = arguments.ToArray();
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--confirm-download", StringComparison.OrdinalIgnoreCase))
            {
                EnqueueConfirmation(args[index + 1]);
                break;
            }
        }
    }

    /// <summary>Starts the bridge once and returns the finite startup task.</summary>
    public Task StartAsync()
    {
        lock (lifecycleSync)
        {
            if (startupTask is not null)
            {
                return startupTask;
            }

            if (stopRequested)
            {
                return Task.CompletedTask;
            }

            viewModel.ActionRequested += OnActionRequested;
            actionHandlerAttached = true;
            StartActivationServer();
            startupTask = StartCoreAsync();
            return startupTask;
        }
    }

    /// <summary>
    /// Adds a job ID to the confirmation queue. Called both from the constructor
    /// (for <c>--confirm-download</c> command-line args) and from the
    /// activation pipe handler (for requests arriving from the Agent).
    /// </summary>
    private void EnqueueConfirmation(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        if (pendingConfirmationSet.TryAdd(jobId, 0))
        {
            pendingConfirmations.Enqueue(jobId);
        }
    }

    private void StartActivationServer()
    {
        if (activationServerTask is not null)
        {
            return;
        }

        var server = new DesktopActivationServer(
            null,
            async (jobId, token) =>
            {
                EnqueueConfirmation(jobId);
                await TryShowNextConfirmationAsync(token).ConfigureAwait(false);
                return true;
            });

        activationServerTask = Task.Run(
            () => server.RunAsync(shutdown.Token),
            CancellationToken.None);
    }

    /// <summary>
    /// Cancels work without waiting. This is safe to call synchronously from an
    /// Avalonia shutdown callback; <see cref="DisposeAsync"/> can be awaited by a
    /// non-UI owner when deterministic cleanup is needed.
    /// </summary>
    public void Stop()
    {
        lock (lifecycleSync)
        {
            if (stopRequested)
            {
                return;
            }

            stopRequested = true;
            if (actionHandlerAttached)
            {
                viewModel.ActionRequested -= OnActionRequested;
                actionHandlerAttached = false;
            }
        }

        shutdown.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Stop();
        Task? startup;
        Task? activationServer;
        lock (lifecycleSync)
        {
            startup = startupTask;
            activationServer = activationServerTask;
        }

        await IgnoreExpectedShutdownAsync(startup).ConfigureAwait(false);
        Task? polling;
        Task[] actions;
        lock (lifecycleSync)
        {
            polling = pollingTask;
        }

        lock (actionTasksSync)
        {
            actions = actionTasks.ToArray();
        }

        await IgnoreExpectedShutdownAsync(activationServer).ConfigureAwait(false);
        await IgnoreExpectedShutdownAsync(polling).ConfigureAwait(false);
        foreach (Task action in actions)
        {
            await IgnoreExpectedShutdownAsync(action).ConfigureAwait(false);
        }

        commandGate.Dispose();
        refreshGate.Dispose();
        shutdown.Dispose();
    }

    private async Task StartCoreAsync()
    {
        try
        {
            await Task.Run(
                () =>
                {
                    EnsureAgentProcess();
                },
                shutdown.Token).ConfigureAwait(false);

            bool connected = await WaitForAgentAsync(shutdown.Token).ConfigureAwait(false);
            await UpdateUiAsync(
                () => viewModel.SetAgentConnection(
                    connected,
                    connected ? null : LocalizationService.Current["Status.AgentUnavailable"])).ConfigureAwait(false);

            AgentSnapshot? initialSnapshot = null;
            if (connected)
            {
                try
                {
                    initialSnapshot = await RefreshSnapshotAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRecoverableConnectionException(exception))
                {
                    await ReportDisconnectedAsync(exception).ConfigureAwait(false);
                }
            }

            if (!shutdown.IsCancellationRequested)
            {
                lock (lifecycleSync)
                {
                    if (!stopRequested && pollingTask is null)
                    {
                        pollingTask = PollAsync(shutdown.Token);
                    }
                }
            }

            if (initialSnapshot is not null)
            {
                await TryShowConfirmationAsync(initialSnapshot, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableConnectionException(exception))
        {
            await ReportDisconnectedAsync(exception).ConfigureAwait(false);
            if (!shutdown.IsCancellationRequested)
            {
                lock (lifecycleSync)
                {
                    if (!stopRequested && pollingTask is null)
                    {
                        pollingTask = PollAsync(shutdown.Token);
                    }
                }
            }
        }
    }

    private void OnActionRequested(object? sender, DesktopActionRequestEventArgs e)
    {
        if (shutdown.IsCancellationRequested)
        {
            return;
        }

        Task action = HandleActionRequestedAsync(e);
        lock (actionTasksSync)
        {
            actionTasks.Add(action);
        }

        _ = action.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (actionTasksSync)
                {
                    actionTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleActionRequestedAsync(DesktopActionRequestEventArgs request)
    {
        bool entered = false;
        try
        {
            await commandGate.WaitAsync(shutdown.Token).ConfigureAwait(false);
            entered = true;
            AgentCommandResult result = await ExecuteActionAsync(request, shutdown.Token).ConfigureAwait(false);
            await UpdateUiAsync(() => viewModel.ReportAgentCommandResult(result.Accepted, result.Reason))
                .ConfigureAwait(false);
            await RefreshSnapshotAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableConnectionException(exception))
        {
            await ReportDisconnectedAsync(exception).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                commandGate.Release();
            }
        }
    }

    private async Task<AgentCommandResult> ExecuteActionAsync(
        DesktopActionRequestEventArgs request,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            DesktopActionKind.CreateDownload when request.Confirmation is { } confirmation =>
                await client.CreateDownloadAsync(
                    confirmation.Url,
                    confirmation.FileName,
                    confirmation.Destination,
                    confirmation.Action == DownloadConfirmationAction.DownloadNow,
                    cancellationToken).ConfigureAwait(false),
            DesktopActionKind.ResumeDownload when request.Download?.JobId is { } resumeId =>
                await client.ChangeJobAsync("download.resume", resumeId, cancellationToken).ConfigureAwait(false),
            DesktopActionKind.PauseDownload when request.Download?.JobId is { } pauseId =>
                await client.ChangeJobAsync("download.pause", pauseId, cancellationToken).ConfigureAwait(false),
            DesktopActionKind.StopDownload when request.Download?.JobId is { } stopId =>
                await client.ChangeJobAsync("download.cancel", stopId, cancellationToken).ConfigureAwait(false),
            DesktopActionKind.DeleteDownload when request.Download?.JobId is { } deleteId =>
                await client.RemoveDownloadAsync(deleteId, deleteDownloadedFile: true, cancellationToken).ConfigureAwait(false),
            DesktopActionKind.ClearCompleted when request.JobIds is { Count: > 0 } jobIds =>
                await RemoveJobsAsync(jobIds, cancellationToken).ConfigureAwait(false),
            DesktopActionKind.StartQueue =>
                await client.SendAsync("queue.start", new { }, cancellationToken).ConfigureAwait(false),
            DesktopActionKind.StopQueue =>
                await client.SendAsync("queue.stop", new { }, cancellationToken).ConfigureAwait(false),
            _ => new AgentCommandResult(false, "missing-job", null, null, null),
        };
    }

    private async Task<AgentCommandResult> RemoveJobsAsync(
        IReadOnlyList<string> jobIds,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = new(true, null, null, null, null);
        foreach (string jobId in jobIds)
        {
            result = await client.ChangeJobAsync("download.remove", jobId, cancellationToken).ConfigureAwait(false);
            if (!result.Accepted)
            {
                break;
            }
        }

        return result;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                AgentSnapshot snapshot = await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                await TryShowConfirmationAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRecoverableConnectionException(exception))
            {
                await ReportDisconnectedAsync(exception).ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AgentCommandResult result = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Accepted || result.Snapshot is not { } snapshot)
            {
                throw new InvalidDataException(result.Reason ?? "The Agent snapshot was rejected.");
            }

            await UpdateUiAsync(() =>
            {
                viewModel.SetAgentConnection(true);
                viewModel.ApplyAgentSnapshot(snapshot);
            }).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task TryShowConfirmationAsync(AgentSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Queue every job that is still waiting for user input so its
        // confirmation dialog is surfaced even when the activation callback
        // raced ahead of the first snapshot (which would otherwise drop the
        // job ID and leave the download stuck at 0%).
        foreach (DownloadJobSnapshot job in snapshot.Jobs)
        {
            if (job.State == DownloadJobState.NeedsInput)
            {
                EnqueueConfirmation(job.Id.ToString());
            }
        }

        // Drain any queued confirmations whose jobs now appear in the snapshot.
        // This path handles both polling-cycle triggers and startup triggers.
        await TryShowNextConfirmationAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryShowNextConfirmationAsync(CancellationToken cancellationToken)
    {
        await TryShowNextConfirmationAsync(snapshot: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryShowNextConfirmationAsync(AgentSnapshot? snapshot, CancellationToken cancellationToken)
    {
        // Only one confirmation dialog at a time on the UI thread.
        if (Interlocked.CompareExchange(ref confirmationShowing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (pendingConfirmations.TryDequeue(out string? jobId))
            {
                pendingConfirmationSet.TryRemove(jobId, out _);

                DownloadJobSnapshot? job = snapshot?.Jobs.FirstOrDefault(item =>
                    string.Equals(item.Id.ToString(), jobId, StringComparison.OrdinalIgnoreCase));
                if (job is null)
                {
                    // The Agent might not have registered the job yet; re-queue and
                    // let the next polling cycle pick it up.
                    if (snapshot is not null)
                    {
                        pendingConfirmationSet.TryAdd(jobId, 0);
                        pendingConfirmations.Enqueue(jobId);
                    }

                    break;
                }

                DownloadConfirmationResult? confirmation = await Dispatcher.UIThread.InvokeAsync(() =>
                    window.ShowPendingDownloadConfirmationAsync(job));
                cancellationToken.ThrowIfCancellationRequested();

                AgentCommandResult result = confirmation is null || confirmation.Action == DownloadConfirmationAction.Cancel
                    ? await client.ChangeJobAsync("download.cancel", jobId, cancellationToken).ConfigureAwait(false)
                    : await client.ConfirmDownloadAsync(
                        jobId,
                        confirmation.Action == DownloadConfirmationAction.DownloadNow,
                        cancellationToken).ConfigureAwait(false);
                await UpdateUiAsync(() => viewModel.ReportAgentCommandResult(result.Accepted, result.Reason))
                    .ConfigureAwait(false);
                await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Put the in-flight job back so it can be retried on the next cycle.
            // (Already removed from the set; re-queue if we had one.)
        }
        finally
        {
            Volatile.Write(ref confirmationShowing, 0);
        }
    }

    private async Task<bool> WaitForAgentAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                AgentCommandResult result = await client.PingAsync(cancellationToken).ConfigureAwait(false);
                if (result.Accepted)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableConnectionException(exception))
            {
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task UpdateUiAsync(Action update)
    {
        if (shutdown.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(update);
    }

    private async Task ReportDisconnectedAsync(Exception exception)
    {
        if (!shutdown.IsCancellationRequested)
        {
            await UpdateUiAsync(() => viewModel.SetAgentConnection(false, exception.Message)).ConfigureAwait(false);
        }
    }

    private static void EnsureAgentProcess()
    {
        Process[] existingProcesses = Process.GetProcessesByName("Correntra.Agent");
        try
        {
            if (existingProcesses.Any(static process => !process.HasExited))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // A process can exit between enumeration and inspection; starting a
            // second copy is safe because the Agent owns a per-user mutex.
        }
        finally
        {
            foreach (Process process in existingProcesses)
            {
                process.Dispose();
            }
        }

        string agentPath = Path.Combine(AppContext.BaseDirectory, "Correntra.Agent.exe");
        if (!File.Exists(agentPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = agentPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Correntra Agent could not be started: {exception.Message}");
        }
    }

    private static bool IsRecoverableConnectionException(Exception exception) =>
        exception is IOException or TimeoutException or JsonException or InvalidOperationException;

    private static async Task IgnoreExpectedShutdownAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsRecoverableConnectionException(exception))
        {
        }
    }
}
