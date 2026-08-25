using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Correntra.Desktop.Services;

namespace Correntra.Desktop.Models;

public sealed class CategoryNode : ObservableObject
{
    private readonly LocalizationService localizer;
    private int count;

    public CategoryNode(
        string localizationKey,
        string iconKind,
        LocalizationService localizer,
        IEnumerable<CategoryNode>? children = null)
    {
        LocalizationKey = localizationKey;
        IconKind = iconKind;
        this.localizer = localizer;
        Children = new ObservableCollection<CategoryNode>(children ?? []);
        localizer.PropertyChanged += (_, _) => OnPropertyChanged(nameof(DisplayName));
    }

    public string LocalizationKey { get; }

    public string IconKind { get; }

    public string DisplayName => localizer[LocalizationKey];

    public ObservableCollection<CategoryNode> Children { get; }

    public int Count
    {
        get => count;
        set => SetProperty(ref count, value);
    }
}

public sealed class DownloadListItem : ObservableObject
{
    private readonly LocalizationService localizer;
    private string stateKey;
    private double progress;
    private string size;
    private string remaining;
    private string speed;
    private string description;

    public DownloadListItem(
        string fileName,
        string category,
        string size,
        string stateKey,
        double progress,
        string remaining,
        string speed,
        string added,
        string description = "",
        string? jobId = null,
        string? destinationPath = null)
    {
        JobId = jobId;
        FileName = fileName;
        Category = category;
        this.size = size;
        this.stateKey = stateKey;
        this.progress = progress;
        this.remaining = remaining;
        this.speed = speed;
        Added = added;
        this.description = description;
        DestinationPath = destinationPath;
        localizer = LocalizationService.Current;
        localizer.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Status));
    }

    public string? JobId { get; }

    public string FileName { get; }

    public string? DestinationPath { get; }

    public string Category { get; }

    public string Size
    {
        get => size;
        private set => SetProperty(ref size, value);
    }

    public string Status => localizer[StateKey];

    public string Remaining
    {
        get => remaining;
        private set => SetProperty(ref remaining, value);
    }

    public string Speed
    {
        get => speed;
        private set => SetProperty(ref speed, value);
    }

    public string Added { get; }

    public string Description
    {
        get => description;
        private set => SetProperty(ref description, value);
    }

    public string ProgressText => $"{Progress:0}%";

    public string StateKey
    {
        get => stateKey;
        set
        {
            if (SetProperty(ref stateKey, value))
            {
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public double Progress
    {
        get => progress;
        set
        {
            if (SetProperty(ref progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public void UpdateRuntime(
        string newSize,
        string newStateKey,
        double newProgress,
        string newRemaining,
        string newSpeed,
        string newDescription)
    {
        Size = newSize;
        StateKey = newStateKey;
        Progress = newProgress;
        Remaining = newRemaining;
        Speed = newSpeed;
        Description = newDescription;
    }

    private string vtStatus = "";
    private bool vtIsThreat;

    /// <summary>One-line VirusTotal verdict shown under the file name.</summary>
    public string VtStatus
    {
        get => vtStatus;
        private set
        {
            if (vtStatus != value)
            {
                vtStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasVtStatus));
            }
        }
    }

    public bool HasVtStatus => !string.IsNullOrEmpty(vtStatus);

    /// <summary>Clean/unknown verdicts use the muted style; threats go red.</summary>
    public bool ShowMutedVt => HasVtStatus && !vtIsThreat;

    public bool VtIsThreat => vtIsThreat;

    public void SetVirusTotalStatus(string status, bool isThreat = false)
    {
        vtIsThreat = isThreat;
        OnPropertyChanged(nameof(VtIsThreat));
        VtStatus = status;
    }
}

public sealed class LocalizedOption : ObservableObject
{
    private readonly LocalizationService localizer;

    public LocalizedOption(string value, string localizationKey)
    {
        Value = value;
        LocalizationKey = localizationKey;
        localizer = LocalizationService.Current;
        localizer.PropertyChanged += (_, _) => OnPropertyChanged(nameof(DisplayName));
    }

    public string Value { get; }

    public string LocalizationKey { get; }

    public string DisplayName => localizer[LocalizationKey];
}

public sealed record AddUrlResult(string Url);

public enum DownloadConfirmationAction
{
    Cancel,
    DownloadNow,
    DownloadLater,
}

public sealed record DownloadConfirmationResult(
    DownloadConfirmationAction Action,
    string Url,
    string FileName,
    string Category,
    string Destination,
    bool RememberForSite);

public sealed record MediaSelectionResult(
    string VideoTrack,
    string AudioTrack,
    string Format,
    string Subtitle,
    bool AudioOnly,
    bool EmbedMetadata);

public sealed record QueueScheduleResult(
    bool IsEnabled,
    TimeSpan StartTime,
    TimeSpan StopTime,
    int ConcurrentDownloads,
    int SpeedLimitKilobytes,
    string CompletionAction);
