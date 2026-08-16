using System.Diagnostics;

namespace Correntra.Platform.Windows.Shell;

public static class ShellLauncher
{
    public static void OpenFile(string filePath)
    {
        string path = RequireExisting(filePath, false);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenFolder(string directoryPath)
    {
        string path = RequireExisting(directoryPath, true);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void RevealFile(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            OpenFolder(Path.GetDirectoryName(RequireExisting(filePath, false))!);
            return;
        }

        string path = RequireExisting(filePath, false);
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"/select,{path}");
        Process.Start(startInfo);
    }

    private static string RequireExisting(string path, bool directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        bool exists = directory ? Directory.Exists(fullPath) : File.Exists(fullPath);
        return exists ? fullPath : throw new FileNotFoundException("The requested shell target was not found.", fullPath);
    }
}

