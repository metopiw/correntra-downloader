using System.Collections.Immutable;
using Correntra.Core.Internal;
using Correntra.Core.Security;

namespace Correntra.Core.Settings;

public enum SupportedLanguage
{
    Turkish = 0,
    English = 1,
}

public enum ApplicationTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}

public enum DistributionMode
{
    Installed = 0,
    Portable = 1,
}

public enum UpdatePreference
{
    Disabled = 0,
    NotifyOnly = 1,
    DownloadWithConfirmation = 2,
}

public static class SupportedLanguageExtensions
{
    public static string ToCultureName(this SupportedLanguage language)
    {
        return language switch
        {
            SupportedLanguage.Turkish => "tr-TR",
            SupportedLanguage.English => "en-US",
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        };
    }

    public static SupportedLanguage FromCultureName(string? cultureName)
    {
        string value = Guard.NotNullOrWhiteSpace(cultureName, nameof(cultureName), 20);
        return value.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
            ? SupportedLanguage.Turkish
            : value.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? SupportedLanguage.English
                : throw new ArgumentException("Only Turkish and English are supported.", nameof(cultureName));
    }
}

public sealed class GeneralSettings
{
    public GeneralSettings(
        SupportedLanguage language,
        ApplicationTheme theme,
        string defaultDownloadDirectory,
        bool startWithWindows = false,
        bool minimizeToTray = true,
        bool monitorClipboard = true,
        bool showCompletionNotifications = true,
        bool showDownloadConfirmation = true)
    {
        if (!Enum.IsDefined(language))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        Language = language;
        Theme = theme;
        DefaultDownloadDirectory = SafePath.CanonicalizeDirectory(defaultDownloadDirectory, nameof(defaultDownloadDirectory));
        StartWithWindows = startWithWindows;
        MinimizeToTray = minimizeToTray;
        MonitorClipboard = monitorClipboard;
        ShowCompletionNotifications = showCompletionNotifications;
        ShowDownloadConfirmation = showDownloadConfirmation;
    }

    public SupportedLanguage Language { get; }

    public ApplicationTheme Theme { get; }

    public string DefaultDownloadDirectory { get; }

    public bool StartWithWindows { get; }

    public bool MinimizeToTray { get; }

    public bool MonitorClipboard { get; }

    public bool ShowCompletionNotifications { get; }

    public bool ShowDownloadConfirmation { get; }
}

public sealed class BrowserIntegrationSettings
{
    public BrowserIntegrationSettings(
        bool isEnabled = true,
        bool shareSiteSessions = true,
        IEnumerable<string>? excludedHosts = null,
        IEnumerable<string>? excludedFileExtensions = null)
    {
        IsEnabled = isEnabled;
        ShareSiteSessions = shareSiteSessions;
        ExcludedHosts = NormalizeHosts(excludedHosts);
        ExcludedFileExtensions = NormalizeExtensions(excludedFileExtensions);
    }

    public bool IsEnabled { get; }

    public bool ShareSiteSessions { get; }

    public ImmutableHashSet<string> ExcludedHosts { get; }

    public ImmutableHashSet<string> ExcludedFileExtensions { get; }

    public bool IsHostExcluded(string host)
    {
        string normalized = Guard.NotNullOrWhiteSpace(host, nameof(host), 253).TrimEnd('.').ToLowerInvariant();
        return ExcludedHosts.Any(pattern =>
            string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase) ||
            (pattern.StartsWith("*.", StringComparison.Ordinal) &&
             normalized.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) &&
             normalized.Length > pattern.Length - 1));
    }

    private static ImmutableHashSet<string> NormalizeHosts(IEnumerable<string>? values)
    {
        ImmutableHashSet<string>.Builder builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values ?? [])
        {
            string pattern = Guard.NotNullOrWhiteSpace(value, nameof(values), 253).TrimEnd('.').ToLowerInvariant();
            string host = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;
            if (Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.IPv6)
            {
                throw new ArgumentException("An excluded host is invalid.", nameof(values));
            }

            builder.Add(pattern);
        }

        return builder.ToImmutable();
    }

    private static ImmutableHashSet<string> NormalizeExtensions(IEnumerable<string>? values)
    {
        ImmutableHashSet<string>.Builder builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values ?? [])
        {
            string extension = Guard.NotNullOrWhiteSpace(value, nameof(values), 32).ToLowerInvariant();
            extension = extension.StartsWith('.') ? extension : "." + extension;
            if (extension.Length < 2 || extension.Skip(1).Any(static character => !char.IsAsciiLetterOrDigit(character)))
            {
                throw new ArgumentException("An excluded file extension is invalid.", nameof(values));
            }

            builder.Add(extension);
        }

        return builder.ToImmutable();
    }
}

public sealed class TransferSettings
{
    public TransferSettings(
        int maxConcurrentDownloads = 4,
        int maxSegmentsPerDownload = 8,
        int retryCount = 8,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? requestTimeout = null,
        long? globalSpeedLimitBytesPerSecond = null)
    {
        if (maxConcurrentDownloads is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentDownloads));
        }

        if (maxSegmentsPerDownload is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSegmentsPerDownload));
        }

        if (retryCount is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        TimeSpan retryDelay = retryBaseDelay ?? TimeSpan.FromSeconds(2);
        if (retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(retryBaseDelay));
        }

        TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(100);
        if (timeout < TimeSpan.FromSeconds(5) || timeout > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (globalSpeedLimitBytesPerSecond is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(globalSpeedLimitBytesPerSecond));
        }

        MaxConcurrentDownloads = maxConcurrentDownloads;
        MaxSegmentsPerDownload = maxSegmentsPerDownload;
        RetryCount = retryCount;
        RetryBaseDelay = retryDelay;
        RequestTimeout = timeout;
        GlobalSpeedLimitBytesPerSecond = globalSpeedLimitBytesPerSecond;
    }

    public int MaxConcurrentDownloads { get; }

    public int MaxSegmentsPerDownload { get; }

    public int RetryCount { get; }

    public TimeSpan RetryBaseDelay { get; }

    public TimeSpan RequestTimeout { get; }

    public long? GlobalSpeedLimitBytesPerSecond { get; }
}

public sealed class PrivacySettings
{
    public PrivacySettings(bool offerCrashReportAfterCrash = true, int localLogRetentionDays = 7)
    {
        if (localLogRetentionDays is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(localLogRetentionDays));
        }

        OfferCrashReportAfterCrash = offerCrashReportAfterCrash;
        LocalLogRetentionDays = localLogRetentionDays;
    }

    public static bool TelemetryEnabled => false;

    public bool OfferCrashReportAfterCrash { get; }

    public int LocalLogRetentionDays { get; }
}

public readonly record struct GitHubRepository
{
    public GitHubRepository(string owner, string name)
    {
        Owner = ValidateSegment(owner, nameof(owner));
        Name = ValidateSegment(name, nameof(name));
    }

    public string Owner { get; }

    public string Name { get; }

    public static GitHubRepository Parse(string value)
    {
        string repository = Guard.NotNullOrWhiteSpace(value, nameof(value), 201);
        string[] segments = repository.Split('/');
        return segments.Length == 2
            ? new GitHubRepository(segments[0], segments[1])
            : throw new FormatException("A GitHub repository must use the owner/name format.");
    }

    public override string ToString() => $"{Owner}/{Name}";

    private static string ValidateSegment(string? value, string parameterName)
    {
        string segment = Guard.NotNullOrWhiteSpace(value, parameterName, 100);
        if (segment.StartsWith('.') ||
            segment.EndsWith('.') ||
            segment.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("A GitHub repository segment contains an invalid character.", parameterName);
        }

        return segment;
    }
}

public sealed class UpdateSettings
{
    public UpdateSettings(
        GitHubRepository repository,
        DistributionMode distributionMode,
        UpdatePreference preference)
    {
        if (string.IsNullOrEmpty(repository.Owner) || string.IsNullOrEmpty(repository.Name))
        {
            throw new ArgumentException("A GitHub repository is required.", nameof(repository));
        }

        if (!Enum.IsDefined(distributionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(distributionMode));
        }

        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (distributionMode == DistributionMode.Portable && preference == UpdatePreference.DownloadWithConfirmation)
        {
            throw new ArgumentException("Portable distributions can notify about releases but cannot self-update.", nameof(preference));
        }

        Repository = repository;
        DistributionMode = distributionMode;
        Preference = preference;
    }

    public GitHubRepository Repository { get; }

    public DistributionMode DistributionMode { get; }

    public UpdatePreference Preference { get; }
}

public sealed class ApplicationSettings
{
    public ApplicationSettings(
        GeneralSettings general,
        BrowserIntegrationSettings browserIntegration,
        TransferSettings transfer,
        PrivacySettings privacy,
        UpdateSettings updates)
    {
        General = general ?? throw new ArgumentNullException(nameof(general));
        BrowserIntegration = browserIntegration ?? throw new ArgumentNullException(nameof(browserIntegration));
        Transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        Privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
    }

    public GeneralSettings General { get; }

    public BrowserIntegrationSettings BrowserIntegration { get; }

    public TransferSettings Transfer { get; }

    public PrivacySettings Privacy { get; }

    public UpdateSettings Updates { get; }

    public static ApplicationSettings CreateDefault(
        string defaultDownloadDirectory,
        DistributionMode distributionMode = DistributionMode.Installed)
    {
        UpdatePreference updatePreference = distributionMode == DistributionMode.Installed
            ? UpdatePreference.DownloadWithConfirmation
            : UpdatePreference.NotifyOnly;

        return new ApplicationSettings(
            new GeneralSettings(SupportedLanguage.Turkish, ApplicationTheme.Dark, defaultDownloadDirectory),
            new BrowserIntegrationSettings(),
            new TransferSettings(),
            new PrivacySettings(),
            new UpdateSettings(
                new GitHubRepository("metopiw", "correntra-downloader"),
                distributionMode,
                updatePreference));
    }
}
