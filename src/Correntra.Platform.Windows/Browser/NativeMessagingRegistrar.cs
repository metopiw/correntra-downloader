using System.Text.Json;
using Correntra.Core;
using Microsoft.Win32;

namespace Correntra.Platform.Windows.Browser;

public sealed record NativeMessagingRegistration(
    string ManifestPath,
    string HostExecutablePath,
    IReadOnlyList<string> AllowedExtensionOrigins);

public static class NativeMessagingRegistrar
{
    public const string HostName = "com.correntra.downloader";
    public const string ExtensionId = BrowserExtensionIdentity.ExtensionId;

    private static readonly string[] RegistryPaths =
    [
        $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}",
        $@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Registers the native messaging host for Chrome and Edge.
    ///
    /// The manifest is written to <paramref name="manifestDirectory"/>. A registry
    /// entry pointing at that manifest is also created for both Chrome and Edge.
    /// Chrome additionally looks for a manifest file directly under
    /// <c>%LOCALAPPDATA%\Google\Chrome\User Data\NativeMessagingHosts\</c>, so a copy
    /// is placed there as well to cover environments where the registry probe is not
    /// performed (for example some managed/policy-restricted Chrome configurations).
    /// </summary>
    public static NativeMessagingRegistration Register(
        string hostExecutablePath,
        string manifestDirectory,
        IEnumerable<string> extensionIds,
        bool allowAllChromeExtensionOrigins = false)
    {
        EnsureWindows();
        string executable = RequireExistingFile(hostExecutablePath, nameof(hostExecutablePath));
        string directory = Path.GetFullPath(manifestDirectory);
        Directory.CreateDirectory(directory);

        var origins = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string id in extensionIds)
        {
            origins.Add($"chrome-extension://{ValidateExtensionId(id)}/");
        }

        if (allowAllChromeExtensionOrigins)
        {
            // Development/sideload convenience: an unpacked extension receives an
            // unpredictable ID, so relaxing to the chrome-extension scheme allows the
            // user to run the extension without knowing its generated ID. The
            // Web Store release keeps the stable ID above, so this is safe in
            // production and still restricts invocation to Chrome/Edge extensions.
            origins.Add("chrome-extension://*/");
        }
        else if (origins.Count == 0)
        {
            throw new ArgumentException("At least one extension ID is required.", nameof(extensionIds));
        }

        string manifestPath = Path.Combine(directory, $"{HostName}.json");
        string temporaryPath = manifestPath + ".tmp";
        var manifest = new
        {
            name = HostName,
            description = "Correntra Downloader browser integration",
            path = executable,
            type = "stdio",
            allowed_origins = origins.ToArray(),
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        File.WriteAllBytes(temporaryPath, json);
        File.Move(temporaryPath, manifestPath, true);

        // Registry entry (primary discovery mechanism for Chrome and Edge).
        foreach (string registryPath in RegistryPaths)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath, true);
            key.SetValue(null, manifestPath, RegistryValueKind.String);
        }

        // Chrome also probes a well-known per-user directory. Mirror the manifest there
        // so that Chrome finds the host even without the registry key being consulted.
        foreach (string probingDirectory in ChromeProbeDirectories())
        {
            try
            {
                Directory.CreateDirectory(probingDirectory);
                string copy = Path.Combine(probingDirectory, $"{HostName}.json");
                File.WriteAllBytes(copy, json);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Non-fatal: the registry entry above is still authoritative for Chrome.
            }
        }

        return new NativeMessagingRegistration(manifestPath, executable, origins.ToArray());
    }

    public static NativeMessagingRegistration RegisterDefault(
        string hostExecutablePath,
        string manifestDirectory,
        bool allowAllChromeExtensionOrigins = false) =>
        Register(hostExecutablePath, manifestDirectory, [ExtensionId], allowAllChromeExtensionOrigins);

    public static void Unregister(string expectedManifestPath)
    {
        EnsureWindows();
        string expected = Path.GetFullPath(expectedManifestPath);
        foreach (string registryPath in RegistryPaths)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(registryPath, false);
            string? current = key?.GetValue(null) as string;
            if (current is not null &&
                string.Equals(Path.GetFullPath(current), expected, StringComparison.OrdinalIgnoreCase))
            {
                Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);
            }
        }

        foreach (string probingDirectory in ChromeProbeDirectories())
        {
            string copy = Path.Combine(probingDirectory, $"{HostName}.json");
            try
            {
                if (File.Exists(copy) &&
                    string.Equals(Path.GetFullPath(copy), expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(copy);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
    }

    private static IEnumerable<string> ChromeProbeDirectories()
    {
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Google", "Chrome", "User Data", "NativeMessagingHosts");
    }

    private static string ValidateExtensionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string id = value.Trim().ToLowerInvariant();
        if (id != "*" && (id.Length != 32 || id.Any(character => character is < 'a' or > 'p')))
        {
            throw new ArgumentException("Chrome/Edge extension IDs must contain 32 characters in the a-p alphabet.", nameof(value));
        }

        return id;
    }

    private static string RequireExistingFile(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("The Native Messaging host executable was not found.", fullPath);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native Messaging registration is available only on Windows.");
        }
    }
}
