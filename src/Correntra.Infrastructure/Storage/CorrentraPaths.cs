namespace Correntra.Infrastructure.Storage;

public sealed record CorrentraPaths
{
    private CorrentraPaths(
        string rootDirectory,
        string databasePath,
        string logsDirectory,
        string temporaryDirectory,
        string credentialsDirectory,
        bool portable)
    {
        RootDirectory = rootDirectory;
        DatabasePath = databasePath;
        LogsDirectory = logsDirectory;
        TemporaryDirectory = temporaryDirectory;
        CredentialsDirectory = credentialsDirectory;
        IsPortable = portable;
    }

    public string RootDirectory { get; }

    public string DatabasePath { get; }

    public string LogsDirectory { get; }

    public string TemporaryDirectory { get; }

    public string CredentialsDirectory { get; }

    public bool IsPortable { get; }

    public static CorrentraPaths Resolve(string? applicationDirectory = null)
    {
        string baseDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        bool portable = File.Exists(Path.Combine(baseDirectory, "portable.mode"));
        string root = portable
            ? Path.Combine(baseDirectory, "data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Correntra",
                "Downloader");

        return Create(root, portable);
    }

    public static CorrentraPaths Create(string rootDirectory, bool portable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        return new CorrentraPaths(
            root,
            Path.Combine(root, "correntra.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "temporary"),
            Path.Combine(root, "credentials"),
            portable);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
        Directory.CreateDirectory(CredentialsDirectory);
    }
}

