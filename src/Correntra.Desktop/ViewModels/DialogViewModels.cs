using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Correntra.Desktop.Models;
using Correntra.Desktop.Services;

namespace Correntra.Desktop.ViewModels;

public partial class AddUrlDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string url = string.Empty;

    [ObservableProperty]
    private bool showValidationError;

    public bool HasValidationError => ShowValidationError && !IsValidUrl(Url);

    public static bool IsValidUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public partial class DownloadConfirmationViewModel : ViewModelBase
{
    private readonly LocalizationService localizer = LocalizationService.Current;

    [ObservableProperty]
    private string url;

    [ObservableProperty]
    private string fileName;

    [ObservableProperty]
    private LocalizedOption selectedCategory;

    [ObservableProperty]
    private string destination;

    [ObservableProperty]
    private bool rememberForSite;

    public DownloadConfirmationViewModel(
        string sourceUrl,
        string? suggestedFileName = null,
        string? suggestedDestination = null)
    {
        Url = sourceUrl;
        FileName = string.IsNullOrWhiteSpace(suggestedFileName) ? GuessFileName(sourceUrl) : suggestedFileName;
        Categories =
        [
            new LocalizedOption("Programs", "Category.Programs"),
            new LocalizedOption("Video", "Category.Video"),
            new LocalizedOption("Music", "Category.Music"),
            new LocalizedOption("Documents", "Category.Documents"),
            new LocalizedOption("Compressed", "Category.Compressed"),
            new LocalizedOption("Images", "Category.Images"),
        ];
        rememberedDestinations = DesktopSettingsStore.Load().CategoryDestinations;
        SelectedCategory = Categories[0];
        Destination = DestinationFor(SelectedCategory.Value);
        if (!string.IsNullOrWhiteSpace(suggestedDestination))
        {
            Destination = suggestedDestination;
        }
    }

    private readonly Dictionary<string, string> rememberedDestinations;

    public ObservableCollection<LocalizedOption> Categories { get; }

    public string SizeText => localizer["Dialog.Confirm.UnknownSize"];

    private static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Correntra");

    private string DestinationFor(string category) =>
        rememberedDestinations.TryGetValue(category, out string? remembered) &&
        !string.IsNullOrWhiteSpace(remembered)
            ? remembered
            : Path.Combine(DefaultRoot, category);

    partial void OnSelectedCategoryChanged(LocalizedOption value)
    {
        // IDM-style "remember this folder for this category": once saved, the
        // same category preselects its folder on every future capture.
        Destination = DestinationFor(value.Value);
    }

    /// <summary>Saves the current folder for the selected category when the
    /// remember switch is set; other settings are preserved.</summary>
    public void PersistDestinationIfRemembered()
    {
        if (!RememberForSite || SelectedCategory is null)
        {
            return;
        }

        DesktopSettings stored = DesktopSettingsStore.Load();
        stored.CategoryDestinations[SelectedCategory.Value] = Destination;
        DesktopSettingsStore.Save(stored);
    }

    private static string GuessFileName(string sourceUrl)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var candidate = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return "download.bin";
    }
}

public partial class MediaSelectionViewModel : ViewModelBase
{
    [ObservableProperty]
    private string selectedVideoTrack = "1080p • AVC • 60 FPS";

    [ObservableProperty]
    private string selectedAudioTrack = "M4A • 256 kbps • Stereo";

    [ObservableProperty]
    private string selectedFormat = "MP4";

    [ObservableProperty]
    private string selectedSubtitle = "Türkçe (tr)";

    [ObservableProperty]
    private bool audioOnly;

    [ObservableProperty]
    private bool embedMetadata = true;

    public IReadOnlyList<string> VideoTracks { get; } =
    [
        "2160p • VP9 • 60 FPS",
        "1440p • VP9 • 60 FPS",
        "1080p • AVC • 60 FPS",
        "1080p • AVC • 30 FPS",
        "720p • AVC • 30 FPS",
        "480p • AVC",
    ];

    public IReadOnlyList<string> AudioTracks { get; } =
    [
        "M4A • 256 kbps • Stereo",
        "Opus • 160 kbps • Stereo",
        "M4A • 128 kbps • Stereo",
    ];

    public IReadOnlyList<string> Formats { get; } = ["MP4", "MKV", "WEBM", "M4A", "MP3"];

    public IReadOnlyList<string> Subtitles { get; } = ["Türkçe (tr)", "English (en)", "Yok / None"];

    public string ApproximateSize => AudioOnly ? "12.6 MB" : "248 MB";

    partial void OnAudioOnlyChanged(bool value) => OnPropertyChanged(nameof(ApproximateSize));
}

public partial class QueueScheduleViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool monday = true;

    [ObservableProperty]
    private bool tuesday = true;

    [ObservableProperty]
    private bool wednesday = true;

    [ObservableProperty]
    private bool thursday = true;

    [ObservableProperty]
    private bool friday = true;

    [ObservableProperty]
    private bool saturday;

    [ObservableProperty]
    private bool sunday;

    [ObservableProperty]
    private TimeSpan startTime = new(1, 0, 0);

    [ObservableProperty]
    private TimeSpan stopTime = new(7, 0, 0);

    [ObservableProperty]
    private int concurrentDownloads = 4;

    [ObservableProperty]
    private int speedLimitKilobytes;

    [ObservableProperty]
    private LocalizedOption selectedCompletionAction;

    public QueueScheduleViewModel()
    {
        CompletionActions =
        [
            new LocalizedOption("None", "Dialog.Schedule.ActionNone"),
            new LocalizedOption("Sleep", "Dialog.Schedule.ActionSleep"),
            new LocalizedOption("Shutdown", "Dialog.Schedule.ActionShutdown"),
        ];
        SelectedCompletionAction = CompletionActions[0];
    }

    public ObservableCollection<LocalizedOption> CompletionActions { get; }
}

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private LocalizedOption selectedTheme;

    [ObservableProperty]
    private LocalizedOption selectedLanguage;

    [ObservableProperty]
    private bool startWithWindows = true;

    [ObservableProperty]
    private bool minimizeToTray = true;

    [ObservableProperty]
    private bool showNotifications = true;

    [ObservableProperty]
    private bool monitorClipboard = true;

    [ObservableProperty]
    private bool captureBrowserDownloads = true;

    [ObservableProperty]
    private bool showMediaPanel = true;

    [ObservableProperty]
    private bool useTemporarySession = true;

    [ObservableProperty]
    private string excludedSites = string.Empty;

    [ObservableProperty]
    private string excludedExtensions = ".pdf; .jpg; .png";

    [ObservableProperty]
    private int concurrentDownloads = 4;

    [ObservableProperty]
    private int segmentsPerDownload = 8;

    [ObservableProperty]
    private int globalSpeedLimit;

    [ObservableProperty]
    private bool retryTemporaryErrors = true;

    [ObservableProperty]
    private bool crashReportsRequireApproval = true;

    [ObservableProperty]
    private bool keepHistory = true;

    [ObservableProperty]
    private bool checkUpdatesAtStartup = true;

    [ObservableProperty]
    private bool includePrereleases;

    public SettingsViewModel()
    {
        Themes =
        [
            new LocalizedOption("Dark", "Menu.DarkTheme"),
            new LocalizedOption("Light", "Menu.LightTheme"),
        ];
        Languages =
        [
            new LocalizedOption("tr", "Menu.Turkish"),
            new LocalizedOption("en", "Menu.English"),
        ];
        SelectedTheme = Themes[0];
        SelectedLanguage = LocalizationService.Current.LanguageCode == "en" ? Languages[1] : Languages[0];
        Load();
    }

    public ObservableCollection<LocalizedOption> Themes { get; }

    public ObservableCollection<LocalizedOption> Languages { get; }

    /// <summary>Fills the dialog from the persisted store; defaults survive a missing file.</summary>
    private void Load()
    {
        DesktopSettings stored = DesktopSettingsStore.Load();
        SelectedTheme = Themes.FirstOrDefault(option => option.Value == stored.Theme) ?? Themes[0];
        SelectedLanguage = Languages.FirstOrDefault(option => option.Value == stored.Language) ?? Languages[0];
        CheckUpdatesAtStartup = stored.CheckUpdatesAtStartup;
        IncludePrereleases = stored.IncludePrereleases;
        ConcurrentDownloads = stored.ConcurrentDownloads;
        SegmentsPerDownload = stored.SegmentsPerDownload;
        GlobalSpeedLimit = stored.GlobalSpeedLimit;
        RetryTemporaryErrors = stored.RetryTemporaryErrors;
        CrashReportsRequireApproval = stored.CrashReportsRequireApproval;
        KeepHistory = stored.KeepHistory;
        ExcludedExtensions = stored.ExcludedExtensions;
        ExcludedSites = stored.ExcludedSites;
    }

    public void Save()
    {
        DesktopSettingsStore.Save(new DesktopSettings
        {
            Theme = SelectedTheme.Value,
            Language = SelectedLanguage.Value,
            CheckUpdatesAtStartup = CheckUpdatesAtStartup,
            IncludePrereleases = IncludePrereleases,
            ConcurrentDownloads = ConcurrentDownloads,
            SegmentsPerDownload = SegmentsPerDownload,
            GlobalSpeedLimit = GlobalSpeedLimit,
            RetryTemporaryErrors = RetryTemporaryErrors,
            CrashReportsRequireApproval = CrashReportsRequireApproval,
            KeepHistory = KeepHistory,
            ExcludedExtensions = ExcludedExtensions,
            ExcludedSites = ExcludedSites,
        });
    }
}
