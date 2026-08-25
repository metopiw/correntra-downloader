using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Correntra.Desktop.Services;

/// <summary>One installed Chromium-family browser the wizard can target.</summary>
public sealed record DetectedBrowser(string Name, string ExePath);

/// <summary>
/// Locates the browser-extension folder that ships next to the app (or, for
/// dev runs, in the repository) and drives the guided installation: opening
/// the folder in Explorer, copying its path to the clipboard and launching
/// the user's Chromium browser on chrome://extensions.
/// </summary>
public static class ExtensionSetupService
{
    /// <summary>Directory containing manifest.json for "Load unpacked".</summary>
    public static string? LocateExtensionFolder()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            // Installed layout: <app>\browser-extension
            string shipped = Path.Combine(baseDirectory, "browser-extension");
            if (File.Exists(Path.Combine(shipped, "manifest.json")))
            {
                return shipped;
            }

            // Velopack installs nest the app one level down; walk up once.
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDirectory));
            if (parent is not null)
            {
                string shippedParent = Path.Combine(parent, "browser-extension");
                if (File.Exists(Path.Combine(shippedParent, "manifest.json")))
                {
                    return shippedParent;
                }
            }

            // Dev run: src/Correntra.Desktop/bin/Debug/… → repo root.
            DirectoryInfo? candidate = new(baseDirectory);
            for (int depth = 0; depth < 6 && candidate is not null; depth++)
            {
                string repoCopy = Path.Combine(candidate.FullName, "browser-extension");
                if (File.Exists(Path.Combine(repoCopy, "manifest.json")))
                {
                    return repoCopy;
                }

                candidate = candidate.Parent;
            }
        }

        return null;
    }

    /// <summary>Finds installed Chromium browsers in common locations.</summary>
    public static IReadOnlyList<DetectedBrowser> DetectBrowsers()
    {
        var browsers = new List<DetectedBrowser>();
        void TryAdd(string name, params string[] paths)
        {
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    browsers.Add(new DetectedBrowser(name, path));
                    return;
                }
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        TryAdd("Chrome",
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
        TryAdd("Edge",
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        TryAdd("Brave",
            Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        return browsers;
    }

    public static bool OpenExtensionFolder(string extensionFolder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{extensionFolder}\"" });
            return true;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static void CopyFolderToClipboard(Avalonia.Controls.TopLevel owner, string extensionFolder)
    {
        try
        {
            _ = owner.Clipboard?.SetTextAsync(extensionFolder);
        }
        catch (COMException)
        {
            // Clipboard can be locked by another process; non-fatal.
        }
    }

    /// <summary>
    /// Launches the preferred browser directly on the extensions page. The
    /// chrome:// scheme cannot be passed via UseShellExecute URL handling, so
    /// we start the browser executable with it as an argument.
    /// </summary>
    public static bool OpenExtensionsPage(IReadOnlyList<DetectedBrowser> browsers)
    {
        foreach (DetectedBrowser browser in browsers)
        {
            try
            {
                Process.Start(new ProcessStartInfo(browser.ExePath)
                {
                    Arguments = "chrome://extensions",
                    UseShellExecute = false,
                });
                return true;
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
            {
                // Try the next detected browser.
            }
        }

        return false;
    }
}
