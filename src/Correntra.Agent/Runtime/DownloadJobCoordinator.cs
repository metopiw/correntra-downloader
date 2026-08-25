using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Core.Security;
using Correntra.Media.Processing;
using Correntra.Transfer;

namespace Correntra.Agent.Runtime;

public sealed class DownloadJobCoordinator : IAsyncDisposable
{
    private const int MaxAutomaticRetries = 5;

    private static readonly DownloadJobState[] NonTerminalStates =
    [
        DownloadJobState.Pending,
        DownloadJobState.Probing,
        DownloadJobState.NeedsInput,
        DownloadJobState.Queued,
        DownloadJobState.Downloading,
        DownloadJobState.Paused,
        DownloadJobState.Verifying,
        DownloadJobState.Finalizing,
        DownloadJobState.Cancelling,
    ];

    private readonly AgentJobRepository _repository;
    private readonly HttpTransferEngine _transferEngine;
    private readonly MediaExecutor? _mediaExecutor;
    private readonly YtDlpExecutor _ytDlpExecutor;
    private readonly int _maximumConcurrentDownloads;
    private readonly Channel<bool> _scheduleSignals;
    private readonly ConcurrentDictionary<JobId, ActiveTransfer> _activeTransfers = new();
    private readonly ConcurrentDictionary<JobId, long> _lastProgressPersist = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _startLock = new();
    private Task? _schedulerTask;
    private int _activeCount;
    private bool _disposed;

    public DownloadJobCoordinator(
        AgentJobRepository repository,
        HttpTransferEngine transferEngine,
        int maximumConcurrentDownloads = 4,
        MediaExecutor? mediaExecutor = null,
        YtDlpExecutor? ytDlpExecutor = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _transferEngine = transferEngine ?? throw new ArgumentNullException(nameof(transferEngine));
        _mediaExecutor = mediaExecutor;
        _ytDlpExecutor = ytDlpExecutor ?? new YtDlpExecutor();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentDownloads, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentDownloads, 32);
        _maximumConcurrentDownloads = maximumConcurrentDownloads;
        _scheduleSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _repository.RecoverInterruptedAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        lock (_startLock)
        {
            _schedulerTask ??= Task.Run(() => SchedulerLoopAsync(_shutdown.Token), CancellationToken.None);
        }

        SignalScheduler();
    }

    public async Task<AgentJobRecord> CreateAsync(
        AgentJobCreation creation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AgentJobRecord job = await _repository.CreateAsync(creation, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (job.State == DownloadJobState.Queued)
        {
            SignalScheduler();
        }

        return job;
    }

    public Task<IReadOnlyList<AgentJobRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken);

    public async Task<bool> PauseAsync(JobId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeTransfers.TryGetValue(id, out ActiveTransfer? active);
        active?.PauseController.Pause();
        try
        {
            bool changed = await _repository.ChangeStateAsync(
                id,
                [DownloadJobState.Pending, DownloadJobState.Queued, DownloadJobState.Downloading],
                DownloadJobState.Paused,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!changed)
            {
                active?.PauseController.Resume();
            }

            return changed;
        }
        catch
        {
            active?.PauseController.Resume();
            throw;
        }
    }

    public async Task<bool> ResumeAsync(JobId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeTransfers.TryGetValue(id, out ActiveTransfer? active))
        {
            bool resumed = await _repository.ChangeStateAsync(
                id,
                [DownloadJobState.Paused],
                DownloadJobState.Downloading,
                DownloadExecutionIntent.RunWhenPossible,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (resumed)
            {
                active.PauseController.Resume();
            }

            return resumed;
        }

        bool queued = await _repository.ChangeStateAsync(
            id,
            [DownloadJobState.Pending, DownloadJobState.NeedsInput, DownloadJobState.Paused],
            DownloadJobState.Queued,
            DownloadExecutionIntent.RunWhenPossible,
            DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (queued)
        {
            SignalScheduler();
        }

        return queued;
    }

    public async Task<bool> CancelAsync(JobId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeTransfers.TryGetValue(id, out ActiveTransfer? active))
        {
            bool cancelling = await _repository.ChangeStateAsync(
                id,
                [DownloadJobState.Downloading, DownloadJobState.Paused, DownloadJobState.Verifying, DownloadJobState.Finalizing],
                DownloadJobState.Cancelling,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (cancelling)
            {
                active.PauseController.Resume();
                active.Cancellation.Cancel();
            }

            return cancelling;
        }

        return await _repository.ChangeStateAsync(
            id,
            [DownloadJobState.Pending, DownloadJobState.NeedsInput, DownloadJobState.Queued, DownloadJobState.Paused],
            DownloadJobState.Cancelled,
            DownloadExecutionIntent.Hold,
            DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(JobId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool retried = await _repository.RetryAsync(id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (retried)
        {
            SignalScheduler();
        }

        return retried;
    }

    public async Task<bool> ConfirmAsync(
        JobId id,
        bool startImmediately,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DownloadJobState nextState = startImmediately ? DownloadJobState.Queued : DownloadJobState.Paused;
        DownloadExecutionIntent intent = startImmediately
            ? DownloadExecutionIntent.RunWhenPossible
            : DownloadExecutionIntent.Hold;
        bool changed = await _repository.ChangeStateAsync(
            id,
            [DownloadJobState.NeedsInput],
            nextState,
            intent,
            DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (changed && startImmediately)
        {
            SignalScheduler();
        }

        return changed;
    }

    public async Task<bool> RemoveAsync(
        JobId id,
        bool deleteDownloadedFile,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AgentJobRecord? job = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        if (_activeTransfers.TryGetValue(id, out ActiveTransfer? active))
        {
            await CancelAsync(id, cancellationToken).ConfigureAwait(false);
            await active.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deleteDownloadedFile && File.Exists(job.DestinationPath))
        {
            File.Delete(job.DestinationPath);
        }

        TryDeleteFile(HttpTransferEngine.GetTemporaryPath(job.DestinationPath));
        TryDeleteFile(HttpTransferEngine.GetCheckpointPath(job.DestinationPath));

        return await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Removing a job must not fail when its partial state was never
            // created (e.g. the destination directory no longer exists).
        }
    }

    public async Task<int> StartMainQueueAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentJobRecord> jobs = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        int changed = 0;
        foreach (AgentJobRecord job in jobs.Where(static job => job.State is DownloadJobState.NeedsInput or DownloadJobState.Paused))
        {
            if (await ResumeAsync(job.Id, cancellationToken).ConfigureAwait(false))
            {
                changed++;
            }
        }

        return changed;
    }

    public async Task<int> StopMainQueueAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentJobRecord> jobs = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        int changed = 0;
        foreach (AgentJobRecord job in jobs.Where(static job => job.State is DownloadJobState.Pending or DownloadJobState.Queued or DownloadJobState.Downloading))
        {
            if (await PauseAsync(job.Id, cancellationToken).ConfigureAwait(false))
            {
                changed++;
            }
        }

        return changed;
    }

    public async Task<AgentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentJobRecord> jobs = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return new AgentSnapshot(DateTimeOffset.UtcNow, jobs.Select(static job => job.ToSnapshot()));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        foreach (ActiveTransfer active in _activeTransfers.Values)
        {
            active.PauseController.Resume();
            active.Cancellation.Cancel();
        }

        if (_schedulerTask is not null)
        {
            try
            {
                await _schedulerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] activeTasks = _activeTransfers.Values.Select(static active => active.Task).ToArray();
        try
        {
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (ActiveTransfer active in _activeTransfers.Values)
        {
            active.Cancellation.Dispose();
        }

        _transferEngine.Dispose();
        _shutdown.Dispose();
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (bool _ in _scheduleSignals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            while (Volatile.Read(ref _activeCount) < _maximumConcurrentDownloads)
            {
                AgentJobRecord? job = await _repository.TryClaimNextAsync(cancellationToken).ConfigureAwait(false);
                if (job is null)
                {
                    break;
                }

                var pauseController = new PauseController();
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Interlocked.Increment(ref _activeCount);
                var active = new ActiveTransfer(pauseController, cancellation);
                if (!_activeTransfers.TryAdd(job.Id, active))
                {
                    cancellation.Dispose();
                    Interlocked.Decrement(ref _activeCount);
                    continue;
                }

                active.Bind(RunTransferAsync(job, pauseController, cancellation));
            }
        }
    }

    private async Task RunTransferAsync(
        AgentJobRecord job,
        PauseController pauseController,
        CancellationTokenSource cancellation)
    {
        try
        {
            // Social platforms (YouTube, Facebook, X, Instagram, ...) serve
            // media through script-generated streams; a plain HTTP fetch would
            // only save a few kilobytes of HTML/manifest. Route them through
            // the yt-dlp engine, which also carries user-picked qualities.
            if (_ytDlpExecutor.IsAvailable && ShouldRunYtDlp(job))
            {
                await RunYtDlpAsync(job, cancellation.Token).ConfigureAwait(false);
                return;
            }

            if (_mediaExecutor is not null && IsHlsOrDash(job.Source))
            {
                await RunMediaAsync(job, cancellation.Token).ConfigureAwait(false);
                return;
            }

            var progress = new InlineProgress<TransferProgress>(value => PersistProgress(job.Id, pauseController, value));
            string effectiveName = await ResolveEffectiveFileNameAsync(job, cancellation.Token).ConfigureAwait(false);
            string destinationFile = ResolveCollisionFreeDestination(
                Path.Combine(job.DestinationDirectory, effectiveName));
            if (!string.Equals(destinationFile, Path.Combine(job.DestinationDirectory, effectiveName), StringComparison.Ordinal))
            {
                effectiveName = Path.GetFileName(destinationFile);
            }
            if (!string.Equals(effectiveName, job.FileName, StringComparison.Ordinal))
            {
                await _repository.UpdateFileNameAsync(job.Id, effectiveName, CancellationToken.None).ConfigureAwait(false);
            }

            var request = new DownloadRequest(job.Source, destinationFile)
            {
                Headers = job.Headers,
                Overwrite = false,
                PauseToken = pauseController.Token,
                Progress = progress,
                MaxSegments = GetConfiguredMaxSegments(),
            };
            DownloadResult result = await _transferEngine.DownloadAsync(request, cancellation.Token).ConfigureAwait(false);
            await _repository.UpdateProgressAsync(
                job.Id,
                DownloadJobState.Completed,
                result.BytesTransferred,
                result.BytesTransferred,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && _shutdown.IsCancellationRequested)
        {
            AgentJobRecord? current = await _repository.GetAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            if (current?.State != DownloadJobState.Paused)
            {
                await _repository.ChangeStateAsync(
                    job.Id,
                    [DownloadJobState.Probing, DownloadJobState.Downloading, DownloadJobState.Verifying, DownloadJobState.Finalizing],
                    DownloadJobState.Queued,
                    DownloadExecutionIntent.RunWhenPossible,
                    DateTimeOffset.UtcNow,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await _repository.ChangeStateAsync(
                job.Id,
                NonTerminalStates,
                DownloadJobState.Cancelled,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A transient network failure must not permanently kill a download;
            // re-queue the job a few times (the checkpoint file keeps progress)
            // before surfacing a hard failure to the user.
            bool autoRetried = IsTransientFailure(exception) &&
                await TryAutoRetryAsync(job, CancellationToken.None).ConfigureAwait(false);
            if (!autoRetried)
            {
                await _repository.ChangeStateAsync(
                    job.Id,
                    NonTerminalStates,
                    DownloadJobState.Failed,
                    DownloadExecutionIntent.Hold,
                    DateTimeOffset.UtcNow,
                    "transfer.failed",
                    GetSafeFailureMessage(exception),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _activeTransfers.TryRemove(job.Id, out _);
            _lastProgressPersist.TryRemove(job.Id, out _);
            cancellation.Dispose();
            Interlocked.Decrement(ref _activeCount);
            SignalScheduler();
        }
    }

    private async Task RunMediaAsync(AgentJobRecord job, CancellationToken cancellationToken)
    {
        string destination = ResolveCollisionFreeDestination(job.DestinationPath);
        if (!string.Equals(destination, job.DestinationPath, StringComparison.Ordinal))
        {
            await _repository.UpdateFileNameAsync(
                job.Id,
                Path.GetFileName(destination),
                CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await _repository.UpdateProgressAsync(
                job.Id,
                DownloadJobState.Downloading,
                job.BytesTransferred,
                job.TotalBytes,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);

            FfmpegRunResult result = await _mediaExecutor!.RemuxAsync(
                job.Source.AbsoluteUri,
                destination,
                job.Headers.Where(static header => !string.Equals(header.Key, YtDlpExecutor.FormatHeader, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(static header => header.Key, static header => header.Value, StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);

            if (result.Succeeded && File.Exists(destination))
            {
                long length = new FileInfo(destination).Length;
                await _repository.UpdateProgressAsync(
                    job.Id,
                    DownloadJobState.Completed,
                    length,
                    length,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                string message = string.IsNullOrWhiteSpace(result.StandardError)
                    ? "The media stream could not be downloaded."
                    : GetSafeFailureMessage(new HttpRequestException(result.StandardError));
                await _repository.ChangeStateAsync(
                    job.Id,
                    NonTerminalStates,
                    DownloadJobState.Failed,
                    DownloadExecutionIntent.Hold,
                    DateTimeOffset.UtcNow,
                    "media.transfer.failed",
                    message,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _repository.ChangeStateAsync(
                job.Id,
                NonTerminalStates,
                DownloadJobState.Cancelled,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _repository.ChangeStateAsync(
                job.Id,
                NonTerminalStates,
                DownloadJobState.Failed,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                "media.transfer.failed",
                GetSafeFailureMessage(exception),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool ShouldRunYtDlp(AgentJobRecord job)
    {
        if (YtDlpExecutor.IsSupportedHost(job.Source))
        {
            return true;
        }

        // Overlay quality picks stamp this header so unknown watch pages still
        // go through the extractor instead of saving the HTML document.
        foreach (KeyValuePair<string, string> header in job.Headers)
        {
            if (string.Equals(header.Key, YtDlpExecutor.FormatHeader, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A YouTube/Instagram-style playlist URL ("...list=PL…" without a "v="
    /// video id, "/playlists/", or a TikTok sound collection) must expand to
    /// every entry instead of yt-dlp's single-item default.
    /// </summary>
    public static bool LooksLikePlaylist(Uri uri)
    {
        string query = uri.Query.ToLowerInvariant();
        if (query.Contains("list=") && !query.Contains("v=", StringComparison.Ordinal) && !query.Contains("video_id", StringComparison.Ordinal))
        {
            return true;
        }

        string path = uri.AbsolutePath.ToLowerInvariant();
        return path.Contains("/playlist") || path.StartsWith("/playlists/", StringComparison.Ordinal);
    }

    private async Task RunYtDlpAsync(AgentJobRecord job, CancellationToken cancellationToken)
    {
        // A playlist expands to one file per entry; park them in a sibling
        // folder named after the job so the category root stays readable.
        bool isPlaylist = LooksLikePlaylist(job.Source);
        string targetDirectory = Path.GetDirectoryName(job.DestinationPath)!;
        if (isPlaylist)
        {
            string folderName = SafePath.SanitizeFileName(Path.GetFileNameWithoutExtension(job.FileName));
            targetDirectory = Path.Combine(targetDirectory, folderName);
            Directory.CreateDirectory(targetDirectory);
            await _repository.UpdateFileNameAsync(
                job.Id,
                Path.Combine(folderName, Path.GetFileName(job.FileName)),
                CancellationToken.None).ConfigureAwait(false);
        }

        // yt-dlp skips existing targets ("has already been downloaded"), which
        // would silently report the OLD file as the fresh download's result;
        // move the target aside first.
        string destination = ResolveCollisionFreeDestination(Path.Combine(targetDirectory, Path.GetFileName(job.DestinationPath)));
        if (!string.Equals(destination, job.DestinationPath, StringComparison.Ordinal))
        {
            await _repository.UpdateFileNameAsync(
                job.Id,
                Path.GetFileName(destination),
                CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await _repository.UpdateProgressAsync(
                job.Id,
                DownloadJobState.Downloading,
                0,
                null,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);

            job.Headers.TryGetValue(YtDlpExecutor.FormatHeader, out string? formatSelector);
            long lastReported = -1;
            long lastTotal = -1;
            long lastReportTicks = 0;
            bool finalizingWritten = false;
            YtDlpDownloadResult result = await _ytDlpExecutor.DownloadAsync(
                job.Source.AbsoluteUri,
                formatSelector,
                destination,
                job.Headers,
                (percent, estimatedTotal) =>
                {
                    // Report at most twice per second; derive byte counters from
                    // yt-dlp's percentage and size estimate so the UI can show
                    // progress, speed and ETA like any other download.
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(lastReportTicks, nowTicks) < TimeSpan.FromMilliseconds(500))
                    {
                        return;
                    }

                    lastReportTicks = nowTicks;
                    long total = estimatedTotal ?? (lastTotal > 0 ? lastTotal : 0);
                    if (total > 0)
                    {
                        lastTotal = total;
                    }

                    long transferred = total > 0 ? (long)(total * percent / 100d) : (long)(percent * 1_000_000);
                    if (total <= 0)
                    {
                        // Without a size estimate keep the synthetic counters in a
                        // 0..100_000_000 window so BytesTransferred <= TotalBytes.
                        total = 100_000_000;
                    }

                    if (transferred > total)
                    {
                        transferred = total;
                    }

                    // Retries and multi-track runs can produce samples that go
                    // backwards or stall at the same value; the visible bar
                    // must never regress.
                    if (transferred <= lastReported)
                    {
                        return;
                    }

                    lastReported = transferred;
                    _ = _repository.UpdateProgressAsync(
                        job.Id,
                        DownloadJobState.Downloading,
                        transferred,
                        total,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                },
                onFinalizing: () =>
                {
                    // Merge/remux emits no progress lines; pin the row to
                    // Finalizing so the user does not read it as a hang.
                    if (finalizingWritten)
                    {
                        return;
                    }

                    finalizingWritten = true;
                    _ = _repository.UpdateProgressAsync(
                        job.Id,
                        DownloadJobState.Finalizing,
                        Math.Max(0, lastReported),
                        lastTotal > 0 ? lastTotal : null,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                },
                allowPlaylist: isPlaylist,
                cancellationToken).ConfigureAwait(false);

            string? finalPath = result.Succeeded ? FindProducedFile(destination) : null;
            if (finalPath is not null && !string.Equals(finalPath, destination, StringComparison.Ordinal))
            {
                File.Delete(destination);
                File.Move(finalPath, destination);
                finalPath = destination;
            }

            if (result.Succeeded && isPlaylist &&
                Directory.Exists(targetDirectory) &&
                Directory.EnumerateFiles(targetDirectory).Any())
            {
                // Entries land as numbered files ("001 - Title.mp4", …); count
                // the whole folder instead of hunting for the -o template.
                long playlistLength = Directory.EnumerateFiles(targetDirectory)
                    .Sum(static path => new FileInfo(path).Length);
                await _repository.UpdateProgressAsync(
                    job.Id,
                    DownloadJobState.Completed,
                    playlistLength,
                    playlistLength,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (result.Succeeded && finalPath is not null && File.Exists(finalPath))
            {
                long length = new FileInfo(finalPath).Length;
                await _repository.UpdateProgressAsync(
                    job.Id,
                    DownloadJobState.Completed,
                    length,
                    length,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                string message = result.Diagnostics.Length == 0
                    ? "The video engine could not download this media."
                    : "Video engine: " + RedactUrls(Tail(result.Diagnostics, 180));
                if (LooksLikeSessionGate(result.Diagnostics))
                {
                    // Bot/age gates read like transport errors; surface the
                    // one action that reliably unblocks the next attempt.
                    message += " YouTube wants a signed-in session: close Chrome so Correntra can reuse its sign-in, then retry.";
                }

                await _repository.ChangeStateAsync(
                    job.Id,
                    NonTerminalStates,
                    DownloadJobState.Failed,
                    DownloadExecutionIntent.Hold,
                    DateTimeOffset.UtcNow,
                    "media.ytdlp.failed",
                    message,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _repository.ChangeStateAsync(
                job.Id,
                NonTerminalStates,
                DownloadJobState.Cancelled,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _repository.ChangeStateAsync(
                job.Id,
                NonTerminalStates,
                DownloadJobState.Failed,
                DownloadExecutionIntent.Hold,
                DateTimeOffset.UtcNow,
                "media.ytdlp.failed",
                GetSafeFailureMessage(exception),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// yt-dlp may pick a container that differs from the guessed extension
    /// (e.g. webm instead of mp4 for audio-only); locate whatever it wrote.
    /// </summary>
    private static string? FindProducedFile(string destination)
    {
        if (File.Exists(destination))
        {
            return destination;
        }

        string directory = Path.GetDirectoryName(destination)!;
        string stem = Path.GetFileNameWithoutExtension(destination);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, stem + ".*")
                .OrderByDescending(static path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault()
            : null;
    }

    private static string Tail(string diagnostics)
    {
        string[] lines = diagnostics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? diagnostics : lines[^1];
    }

    private static string Tail(string diagnostics, int maxChars)
    {
        string line = Tail(diagnostics).Trim();
        return line.Length <= maxChars ? line : line[..maxChars];
    }

    /// <summary>
    /// YouTube rejects stream downloads with bot/age gates that look like
    /// transport errors; detect them so the failure message can point at the
    /// actual remedy (a signed-in session from a closed-browser cookie read).
    /// </summary>
    private static bool LooksLikeSessionGate(string diagnostics) =>
        diagnostics.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips query strings (signed tokens) from URLs in diagnostics.</summary>
    private static string RedactUrls(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            @"https?://[^\s""']",
            static match => Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri) ? uri.Host : "[url]");

    private static bool IsHlsOrDash(Uri source)
    {
        string path = source.AbsolutePath;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".m3u8" or ".mpd";
    }

    private async Task<bool> TryAutoRetryAsync(AgentJobRecord job, CancellationToken cancellationToken)
    {
        try
        {
            AgentJobRecord? current = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (current is null || current.AttemptNumber >= MaxAutomaticRetries)
            {
                return false;
            }

            TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, current.AttemptNumber - 1)));
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            bool retried = await _repository.RequeueAsync(job.Id, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            if (retried)
            {
                SignalScheduler();
            }

            return retried;
        }
        catch (Exception)
        {
            // Retry bookkeeping must never mask the original failure.
            return false;
        }
    }

    private static bool IsTransientFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or EndOfStreamException ||
        exception is TransferException and not (
            InvalidRangeResponseException or RemoteResourceChangedException or HashMismatchException);

    /// <summary>
    /// Placeholder names like "download" (no extension) make finished files
    /// useless; ask the server (Content-Disposition / final URL / MIME type)
    /// for a meaningful name before the transfer starts.
    /// </summary>
    private async Task<string> ResolveEffectiveFileNameAsync(AgentJobRecord job, CancellationToken cancellationToken)
    {
        if (!IsGenericFileName(job.FileName))
        {
            return job.FileName;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            RemoteResourceInfo info = await _transferEngine.ProbeAsync(
                job.Source,
                job.Headers,
                new RetryOptions { MaxAttempts = 2 },
                cancellationToken: budget.Token).ConfigureAwait(false);

            string suggested = SafePath.SanitizeFileName(info.SuggestedFileName);
            if (HasKnownExtension(suggested))
            {
                return suggested;
            }

            string? extension = MimeTypeExtension(info.ContentType);
            if (extension is not null)
            {
                string stem = Path.GetFileNameWithoutExtension(job.FileName);
                if (string.IsNullOrWhiteSpace(stem) || string.Equals(stem, "download", StringComparison.OrdinalIgnoreCase))
                {
                    stem = "download";
                }

                return SafePath.SanitizeFileName(stem + extension);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TimeoutException or TransferException or OperationCanceledException)
        {
            // Naming is best-effort; the transfer proceeds with the old name.
        }

        return job.FileName;
    }

    private static bool IsGenericFileName(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length is < 2 or > 10;
    }

    private static bool HasKnownExtension(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length >= 2 && extension.Length <= 10 &&
               extension.Skip(1).All(char.IsAsciiLetterOrDigit);
    }

    private static string? MimeTypeExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "application/x-msdownload" or "application/x-dosexec" or "application/x-msdos-file" => ".exe",
        "application/zip" or "application/x-zip-compressed" => ".zip",
        "application/x-rar-compressed" or "application/vnd.rar" => ".rar",
        "application/gzip" => ".gz",
        "application/x-7z-compressed" => ".7z",
        "application/pdf" => ".pdf",
        "application/x-iso9660-image" => ".iso",
        "application/vnd.android.package-archive" => ".apk",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/x-matroska" => ".mkv",
        "audio/mpeg" => ".mp3",
        "audio/mp4" => ".m4a",
        "audio/ogg" => ".ogg",
        "text/html" => ".html",
        _ => null,
    };

    private void PersistProgress(JobId id, PauseController pauseController, TransferProgress progress)
    {
        DownloadJobState state = pauseController.IsPaused ? DownloadJobState.Paused : progress.Phase switch
        {
            TransferPhase.Paused => DownloadJobState.Paused,
            TransferPhase.Verifying => DownloadJobState.Verifying,
            TransferPhase.Finalizing => DownloadJobState.Finalizing,
            TransferPhase.Completed => DownloadJobState.Finalizing,
            _ => DownloadJobState.Downloading,
        };

        // Progress arrives roughly every 100 ms; writing each sample to SQLite
        // would stall the transfer thread. Persist at most twice per second and
        // always persist state transitions.
        bool important = state is DownloadJobState.Paused or DownloadJobState.Verifying or DownloadJobState.Finalizing;
        long now = Stopwatch.GetTimestamp();
        if (!important &&
            _lastProgressPersist.TryGetValue(id, out long last) &&
            Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _lastProgressPersist[id] = now;
        _ = _repository.UpdateProgressAsync(
            id,
            state,
            progress.BytesTransferred,
            progress.TotalBytes,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private static string GetSafeFailureMessage(Exception exception)
    {
        string message = exception switch
        {
            UnauthorizedAccessException => "The destination is not writable.",
            IOException => "The file could not be written.",
            HttpRequestException => "The remote server could not be reached.",
            _ => "The download could not be completed.",
        };
        return message;
    }

    /// <summary>
    /// IDM-style collision handling: a target file that already exists must
    /// not abort the transfer (the engine refuses to overwrite, which read as
    /// "The file could not be written" at 0 bytes on every retry), so pick
    /// the next free "name (2).ext" slot like IDM does.
    /// </summary>
    private static string ResolveCollisionFreeDestination(string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return destinationPath;
        }

        string directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(destinationPath);
        string extension = Path.GetExtension(destinationPath);
        for (int attempt = 2; attempt < 1000; attempt++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({attempt}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return destinationPath;
    }

    private static int GetConfiguredMaxSegments()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Correntra",
                "Downloader",
                "desktop-settings.json");
            if (!File.Exists(path)) return 8;
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("SegmentsPerDownload", out JsonElement el) &&
                el.TryGetInt32(out int v) && v >= 1 && v <= 32) return v;
            if (doc.RootElement.TryGetProperty("segmentsPerDownload", out JsonElement el2) &&
                el2.TryGetInt32(out int v2) && v2 >= 1 && v2 <= 32) return v2;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return 8;
    }

    private void SignalScheduler() => _scheduleSignals.Writer.TryWrite(true);

    private sealed class ActiveTransfer
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveTransfer(PauseController pauseController, CancellationTokenSource cancellation)
        {
            PauseController = pauseController;
            Cancellation = cancellation;
        }

        public PauseController PauseController { get; }

        public CancellationTokenSource Cancellation { get; }

        public Task Task => _completion.Task;

        public void Bind(Task task)
        {
            ArgumentNullException.ThrowIfNull(task);
            _ = CompleteAsync(task);
        }

        private async Task CompleteAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
                _completion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public InlineProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
