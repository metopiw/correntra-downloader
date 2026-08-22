using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Desktop.Models;
using Correntra.Desktop.Services;

namespace Correntra.Desktop.ViewModels;

public enum DesktopDialogKind
{
    AddUrl,
    MediaSelection,
    Settings,
    Scheduler,
    About,
}

public sealed class DialogRequestEventArgs : EventArgs
{
    public DialogRequestEventArgs(DesktopDialogKind kind)
    {
        Kind = kind;
    }

    public DesktopDialogKind Kind { get; }
}

public enum DesktopActionKind
{
    CreateDownload,
    ResumeDownload,
    PauseDownload,
    StopDownload,
    DeleteDownload,
    ClearCompleted,
    StartQueue,
    StopQueue,
}

public sealed class DesktopActionRequestEventArgs : EventArgs
{
    public DesktopActionRequestEventArgs(
        DesktopActionKind kind,
        DownloadListItem? download = null,
        DownloadConfirmationResult? confirmation = null,
        IReadOnlyList<string>? jobIds = null)
    {
        Kind = kind;
        Download = download;
        Confirmation = confirmation;
        JobIds = jobIds;
    }

    public DesktopActionKind Kind { get; }

    public DownloadListItem? Download { get; }

    public DownloadConfirmationResult? Confirmation { get; }

    public IReadOnlyList<string>? JobIds { get; }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly LocalizationService localizer;
    private readonly Dictionary<string, SpeedSample> speedSamples = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadListEmpty))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private DownloadListItem? selectedDownload;

    [ObservableProperty]
    private CategoryNode? selectedCategory;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isDarkTheme = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserCaptureStatus))]
    private bool isBrowserCaptureConnected;

    [ObservableProperty]
    private long aggregateBytesPerSecond;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public MainViewModel()
    {
        localizer = LocalizationService.Current;
        Categories = BuildCategories(localizer);
        Downloads = [];
        Downloads.CollectionChanged += OnDownloadsChanged;
        localizer.PropertyChanged += OnLanguageChanged;
        StatusMessage = localizer["Status.Ready"];
        SelectedCategory = Categories[0];
    }

    public event EventHandler<DialogRequestEventArgs>? DialogRequested;

    /// <summary>
    /// Typed integration seam for the background agent. The desktop shell keeps
    /// commands responsive while an IPC adapter can translate these requests to
    /// asynchronous agent commands without coupling the UI to transport details.
    /// </summary>
    public event EventHandler<DesktopActionRequestEventArgs>? ActionRequested;

    public ObservableCollection<CategoryNode> Categories { get; }

    public ObservableCollection<DownloadListItem> Downloads { get; }

    public bool IsDownloadListEmpty => Downloads.Count == 0;

    public string TotalCountText => string.Format(
        CultureInfo.CurrentCulture,
        localizer["Status.Total"],
        Downloads.Count);

    public string ActiveCountText => string.Format(
        CultureInfo.CurrentCulture,
        localizer["Status.Active"],
        Downloads.Count(static item => item.StateKey == "State.Downloading"));

    public string AggregateSpeedText => string.Format(
        CultureInfo.CurrentCulture,
        localizer["Status.Speed"],
        FormatRate(AggregateBytesPerSecond));

    public string BrowserCaptureStatus => localizer[
        IsBrowserCaptureConnected ? "Status.ExtensionConnected" : "Status.ExtensionDisconnected"];

    public void SubmitDownload(DownloadConfirmationResult confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ActionRequested?.Invoke(
            this,
            new DesktopActionRequestEventArgs(DesktopActionKind.CreateDownload, confirmation: confirmation));

        StatusMessage = confirmation.Action == DownloadConfirmationAction.DownloadNow
            ? localizer["Dialog.Confirm.Now"]
            : localizer["Dialog.Confirm.Later"];
    }

    public void ApplyAgentSnapshot(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? selectedId = SelectedDownload?.JobId;
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        var aggregateSpeed = 0L;
        foreach (DownloadJobSnapshot job in snapshot.Jobs)
        {
            string id = job.Id.ToString();
            liveIds.Add(id);
            string stateKey = ToStateKey(job.State);
            double progress = job.TotalBytes is > 0
                ? Math.Clamp(job.BytesTransferred * 100d / job.TotalBytes.Value, 0, 100)
                : 0;
            string size = job.TotalBytes is { } total ? FormatBytes(total) : localizer["Dialog.Confirm.UnknownSize"];
            string description = Uri.TryCreate(job.SourceDisplayUri, UriKind.Absolute, out Uri? source)
                ? source.Host
                : job.SourceDisplayUri;
            (string speed, string remaining) = MeasureSpeed(id, job, stateKey);
            if (stateKey == "State.Downloading")
            {
                aggregateSpeed += speedSamples.TryGetValue(id, out SpeedSample latest) && latest.BytesPerSecond > 0
                    ? latest.BytesPerSecond
                    : 0;
            }

            DownloadListItem? existing = Downloads.FirstOrDefault(item => item.JobId == id);
            if (existing is null)
            {
                string category = InferCategory(job.DestinationDirectory);
                existing = new DownloadListItem(
                    job.FileName,
                    category,
                    size,
                    stateKey,
                    progress,
                    remaining,
                    speed,
                    job.UpdatedAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
                    description,
                    id,
                    Path.Combine(job.DestinationDirectory, job.FileName));
                Downloads.Add(existing);
            }
            else
            {
                existing.UpdateRuntime(size, stateKey, progress, remaining, speed, description);
            }
        }

        foreach (DownloadListItem stale in Downloads.Where(item => item.JobId is not null && !liveIds.Contains(item.JobId)).ToArray())
        {
            Downloads.Remove(stale);
        }

        foreach (string staleSample in speedSamples.Keys.Where(key => !liveIds.Contains(key)).ToArray())
        {
            speedSamples.Remove(staleSample);
        }

        AggregateBytesPerSecond = aggregateSpeed;
        SelectedDownload = selectedId is null
            ? SelectedDownload
            : Downloads.FirstOrDefault(item => item.JobId == selectedId);
        RefreshSummary();
    }

    /// <summary>
    /// Derives a windowed transfer speed from consecutive agent snapshots. The
    /// agent only persists byte counters, so the speed is computed here from the
    /// delta between samples instead of trusting a long-running average.
    /// </summary>
    private (string Speed, string Remaining) MeasureSpeed(string jobId, DownloadJobSnapshot job, string stateKey)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (stateKey != "State.Downloading")
        {
            speedSamples.Remove(jobId);
            return ("—", "—");
        }

        long bytesPerSecond = 0;
        if (speedSamples.TryGetValue(jobId, out SpeedSample previous))
        {
            double seconds = (now - previous.SampledAtUtc).TotalSeconds;
            if (seconds >= 0.5)
            {
                long delta = job.BytesTransferred - previous.BytesTransferred;
                bytesPerSecond = delta > 0 ? (long)(delta / seconds) : 0;
                speedSamples[jobId] = new SpeedSample(job.BytesTransferred, now, bytesPerSecond);
            }
        }
        else
        {
            speedSamples[jobId] = new SpeedSample(job.BytesTransferred, now, 0);
        }

        string speed = bytesPerSecond > 0 ? FormatRate(bytesPerSecond) : "—";
        string remaining = "—";
        if (bytesPerSecond > 0 && job.TotalBytes is { } total && total > job.BytesTransferred)
        {
            remaining = FormatEta(TimeSpan.FromSeconds((total - job.BytesTransferred) / (double)bytesPerSecond));
        }

        return (speed, remaining);
    }

    public void SetAgentConnection(bool connected, string? message = null)
    {
        bool wasConnected = IsBrowserCaptureConnected;
        IsBrowserCaptureConnected = connected;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }
        else if (connected && !wasConnected)
        {
            StatusMessage = localizer["Status.Ready"];
        }
    }

    public void ReportAgentCommandResult(bool accepted, string? reason)
    {
        IsBrowserCaptureConnected = true;
        StatusMessage = accepted
            ? localizer["Status.CommandAccepted"]
            : string.Format(
                CultureInfo.CurrentCulture,
                localizer["Status.CommandRejected"],
                string.IsNullOrWhiteSpace(reason) ? localizer["Status.UnknownError"] : reason);
    }

    [RelayCommand]
    private void AddUrl() => RequestDialog(DesktopDialogKind.AddUrl);

    [RelayCommand]
    private void ShowMedia() => RequestDialog(DesktopDialogKind.MediaSelection);

    [RelayCommand]
    private void ShowOptions() => RequestDialog(DesktopDialogKind.Settings);

    [RelayCommand]
    private void ShowScheduler() => RequestDialog(DesktopDialogKind.Scheduler);

    [RelayCommand]
    private void ShowAbout() => RequestDialog(DesktopDialogKind.About);

    [RelayCommand(CanExecute = nameof(HasSelectedDownload))]
    private void Resume()
    {
        if (SelectedDownload is not { } download)
        {
            return;
        }

        ActionRequested?.Invoke(this, new DesktopActionRequestEventArgs(DesktopActionKind.ResumeDownload, download));
        StatusMessage = localizer["Menu.Resume"] + ": " + download.FileName;
        RefreshSummary();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDownload))]
    private void Pause()
    {
        if (SelectedDownload is not { } download)
        {
            return;
        }

        ActionRequested?.Invoke(this, new DesktopActionRequestEventArgs(DesktopActionKind.PauseDownload, download));
        StatusMessage = localizer["Menu.Pause"] + ": " + download.FileName;
        RefreshSummary();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDownload))]
    private void Stop()
    {
        if (SelectedDownload is not { } download)
        {
            return;
        }

        ActionRequested?.Invoke(this, new DesktopActionRequestEventArgs(DesktopActionKind.StopDownload, download));
        StatusMessage = localizer["Menu.Stop"] + ": " + download.FileName;
        RefreshSummary();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDownload))]
    private void Delete()
    {
        if (SelectedDownload is not { } download)
        {
            return;
        }

        ActionRequested?.Invoke(this, new DesktopActionRequestEventArgs(DesktopActionKind.DeleteDownload, download));
        StatusMessage = localizer["Menu.Delete"] + ": " + download.FileName;
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        string[] completedIds = Downloads
            .Where(static row => row.StateKey == "State.Completed" && row.JobId is not null)
            .Select(static row => row.JobId!)
            .ToArray();
        if (completedIds.Length > 0)
        {
            ActionRequested?.Invoke(
                this,
                new DesktopActionRequestEventArgs(DesktopActionKind.ClearCompleted, jobIds: completedIds));
        }

        StatusMessage = localizer["Menu.DeleteCompleted"];
    }

    [RelayCommand]
    private void StartQueue()
    {
        ActionRequested?.Invoke(this, new DesktopActionRequestEventArgs(DesktopActionKind.StartQueue));
        StatusMessage = localizer["Toolbar.StartQueue"];
    }

    [RelayCommand]
    private void StopQueue()
    {
        ActionRequested?.Invoke(this, new DesktopActionRequestEventArgs(DesktopActionKind.StopQueue));
        StatusMessage = localizer["Toolbar.StopQueue"];
    }

    [RelayCommand]
    private void UseDarkTheme()
    {
        IsDarkTheme = true;
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = ThemeVariant.Dark;
        }
    }

    [RelayCommand]
    private void UseLightTheme()
    {
        IsDarkTheme = false;
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    [RelayCommand]
    private void UseTurkish() => localizer.SetLanguage("tr");

    [RelayCommand]
    private void UseEnglish() => localizer.SetLanguage("en");

    [RelayCommand]
    private void SelectAll()
    {
        SelectedDownload = Downloads.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDownload))]
    private void OpenFolder()
    {
        if (SelectedDownload is not { } download || string.IsNullOrWhiteSpace(download.DestinationPath))
        {
            return;
        }

        try
        {
            if (File.Exists(download.DestinationPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + download.DestinationPath + "\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                string? directory = Path.GetDirectoryName(download.DestinationPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = directory,
                        UseShellExecute = true,
                    });
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusMessage = localizer["Status.CannotOpenFolder"];
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDownload))]
    private void OpenFile()
    {
        if (SelectedDownload is not { } download || string.IsNullOrWhiteSpace(download.DestinationPath))
        {
            return;
        }

        try
        {
            if (File.Exists(download.DestinationPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = download.DestinationPath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusMessage = localizer["Status.CannotOpenFile"];
        }
    }

    private bool HasSelectedDownload() => SelectedDownload is not null;

    private void RequestDialog(DesktopDialogKind kind) =>
        DialogRequested?.Invoke(this, new DialogRequestEventArgs(kind));

    private void OnDownloadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsDownloadListEmpty));
        RefreshSummary();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        StatusMessage = localizer["Status.Ready"];
        OnPropertyChanged(nameof(BrowserCaptureStatus));
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(ActiveCountText));
        OnPropertyChanged(nameof(AggregateSpeedText));
    }

    partial void OnAggregateBytesPerSecondChanged(long value) => OnPropertyChanged(nameof(AggregateSpeedText));

    private static string ToStateKey(DownloadJobState state) => state switch
    {
        DownloadJobState.Completed => "State.Completed",
        DownloadJobState.Finalizing => "State.Finalizing",
        DownloadJobState.Downloading or DownloadJobState.Probing or DownloadJobState.Verifying => "State.Downloading",
        DownloadJobState.NeedsInput => "State.NeedsInput",
        DownloadJobState.Queued or DownloadJobState.Pending => "State.Queued",
        DownloadJobState.Paused => "State.Paused",
        DownloadJobState.Failed => "State.Failed",
        DownloadJobState.Cancelled or DownloadJobState.Cancelling => "State.Cancelled",
        _ => "State.Queued",
    };

    private string InferCategory(string destinationDirectory)
    {
        string leaf = Path.GetFileName(destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string key = leaf.ToLowerInvariant() switch
        {
            "video" => "Category.Video",
            "music" => "Category.Music",
            "documents" => "Category.Documents",
            "compressed" => "Category.Compressed",
            "images" => "Category.Images",
            _ => "Category.Programs",
        };
        return localizer[key];
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatRate(long bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/sn";

    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }

        return $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }

    private readonly record struct SpeedSample(long BytesTransferred, DateTimeOffset SampledAtUtc, long BytesPerSecond);

    private static ObservableCollection<CategoryNode> BuildCategories(LocalizationService localizer)
    {
        return
        [
            new CategoryNode(
                "Category.All",
                "all",
                localizer,
                [
                    new CategoryNode("Category.Compressed", "archive", localizer),
                    new CategoryNode("Category.Documents", "document", localizer),
                    new CategoryNode("Category.Music", "music", localizer),
                    new CategoryNode("Category.Programs", "program", localizer),
                    new CategoryNode("Category.Video", "video", localizer),
                    new CategoryNode("Category.Images", "image", localizer),
                ]),
            new CategoryNode("Category.Unfinished", "pending", localizer),
            new CategoryNode("Category.Finished", "complete", localizer),
            new CategoryNode("Category.Grabber", "globe", localizer),
            new CategoryNode(
                "Category.Queues",
                "queue",
                localizer,
                [new CategoryNode("Category.MainQueue", "queue", localizer)]),
        ];
    }
}

/// <summary>Preview-only content; the runtime view model always starts with a clean list.</summary>
public sealed class DesignMainViewModel : MainViewModel
{
    public DesignMainViewModel()
    {
        Downloads.Add(new DownloadListItem(
            "Correntra_Windows_x64.zip",
            "Programlar",
            "186 MB",
            "State.Downloading",
            67,
            "00:18",
            "8.7 MB/sn",
            "20:42",
            "github.com"));
        Downloads.Add(new DownloadListItem(
            "design-foundations.mp4",
            "Video",
            "1.42 GB",
            "State.Paused",
            31,
            "—",
            "0 B/sn",
            "20:38",
            "1080p • H.264"));
        Downloads.Add(new DownloadListItem(
            "ambient-session.m4a",
            "Müzik",
            "42.8 MB",
            "State.Completed",
            100,
            "00:00",
            "—",
            "19:55",
            "M4A • 256 kb/sn"));
        SelectedDownload = Downloads[0];
    }
}
