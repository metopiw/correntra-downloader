using System.Globalization;
using System.Text;

namespace Correntra.Infrastructure.Logging;

public sealed class LocalLogWriter : IAsyncDisposable
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const int MaximumFiles = 5;
    private readonly string _logsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalLogWriter(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _logsDirectory = Path.GetFullPath(logsDirectory);
        Directory.CreateDirectory(_logsDirectory);
    }

    public async Task WriteAsync(string level, string component, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentNullException.ThrowIfNull(message);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(_logsDirectory, "correntra.log");
            RotateIfNeeded(path);
            string safeMessage = SecretRedactor.Redact(message)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} [{level}] {component}: {safeMessage}{Environment.NewLine}");
            await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes)
        {
            return;
        }

        string directory = Path.GetDirectoryName(path)!;
        for (int index = MaximumFiles - 1; index >= 1; index--)
        {
            string source = index == 1 ? path : Path.Combine(directory, $"correntra.{index - 1}.log");
            string destination = Path.Combine(directory, $"correntra.{index}.log");
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }
    }
}

