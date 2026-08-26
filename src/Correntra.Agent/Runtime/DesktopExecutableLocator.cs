namespace Correntra.Agent.Runtime;

/// <summary>
/// Resolves the desktop shell executable path from the Agent's own location.
/// Handles both the packaged layout (all executables side by side) and the
/// development layout (sibling project output under <c>src/Correntra.Desktop</c>).
/// </summary>
public static class DesktopExecutableLocator
{
    private const string DesktopExecutableName = "Correntra.exe";
    private const string DesktopProjectBinDirectory = "Correntra.Desktop";

    public static string? Resolve(string baseDirectory, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string fullPath = Path.GetFullPath(explicitPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string alongside = Path.Combine(baseDirectory, DesktopExecutableName);
        if (File.Exists(alongside))
        {
            return Path.GetFullPath(alongside);
        }

        return FindInDevelopmentTree(baseDirectory);
    }

    private static string? FindInDevelopmentTree(string baseDirectory)
    {
        string? directory = Path.GetFullPath(baseDirectory);
        for (int depth = 0; directory is not null && depth < 10; depth++, directory = Path.GetDirectoryName(directory))
        {
            string desktopBin = Path.Combine(directory, "src", DesktopProjectBinDirectory, "bin");
            if (!Directory.Exists(desktopBin))
            {
                continue;
            }

            string? newest = Directory
                .EnumerateFiles(desktopBin, DesktopExecutableName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                return newest;
            }
        }

        return null;
    }
}
