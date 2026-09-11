using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Services;

/// <summary>
/// Persists the Options dialog across restarts. Before this store existed the
/// Save button only applied theme and language in memory; every other choice
/// (including update checks) silently reset on each launch.
/// </summary>
public static class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Correntra",
                "Downloader");
            return Path.Combine(directory, "desktop-settings.json");
        }
    }

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), SerializerOptions) ?? new DesktopSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or locked settings file must not block startup.
        }

        return new DesktopSettings();
    }

    public static void Save(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(FilePath);
        if (directory is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
    }
}

public sealed class DesktopSettings
{
    public string Theme { get; set; } = "Dark";

    public string Language { get; set; } = "tr";

    public bool VirusTotalEnabled { get; set; }

    public string? VirusTotalApiKey { get; set; }

    public bool CheckUpdatesAtStartup { get; set; } = true;

    public bool IncludePrereleases { get; set; }

    public int ConcurrentDownloads { get; set; } = 4;

    public int SegmentsPerDownload { get; set; } = 8;

    public int GlobalSpeedLimit { get; set; }

    public bool RetryTemporaryErrors { get; set; } = true;

    public bool CrashReportsRequireApproval { get; set; } = true;

    public bool KeepHistory { get; set; } = true;

    public string ExcludedExtensions { get; set; } = ".pdf; .jpg; .png";

    public string ExcludedSites { get; set; } = string.Empty;

    /// <summary>Category → folder map saved from the IDM-style "remember this
    /// folder for this category" switch in the capture dialog.</summary>
    public Dictionary<string, string> CategoryDestinations { get; set; } = new(StringComparer.Ordinal);
}
