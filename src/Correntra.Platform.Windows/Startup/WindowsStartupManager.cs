using Microsoft.Win32;

namespace Correntra.Platform.Windows.Startup;

public static class WindowsStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Correntra Downloader";

    public static void SetEnabled(bool enabled, string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            string fullPath = Path.GetFullPath(executablePath);
            key.SetValue(ValueName, $"\"{fullPath}\" --background", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string;
    }
}
