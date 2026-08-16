using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Correntra.Media.Processing;

public sealed record FfmpegProgress(
    TimeSpan? Processed,
    long? TotalBytes,
    double? Speed,
    bool IsCompleted,
    string? RawStatus);

public sealed record FfmpegRunResult(int ExitCode, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class FfmpegRunner
{
    private const int MaximumDiagnosticCharacters = 256 * 1024;

    public static async Task<FfmpegRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg process could not be started.");
        }

        var diagnostic = new StringBuilder();
        Task outputDrain = DrainOutputAsync(process.StandardOutput, cancellationToken);
        Task progressTask = ReadProgressAsync(process.StandardError, diagnostic, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputDrain, progressTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TrySendQuit(process);
            if (!process.WaitForExit(1500))
            {
                process.Kill(true);
            }

            throw;
        }

        progress?.Report(new FfmpegProgress(null, null, null, process.ExitCode == 0, "end"));
        return new FfmpegRunResult(process.ExitCode, diagnostic.ToString());
    }

    private static async Task DrainOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        StringBuilder diagnostic,
        IProgress<FfmpegProgress>? progress,
        CancellationToken cancellationToken)
    {
        TimeSpan? processed = null;
        long? totalBytes = null;
        double? speed = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            AppendLimited(diagnostic, line);
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals];
            string value = line[(equals + 1)..];
            switch (key)
            {
                case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds):
                    processed = TimeSpan.FromTicks(checked(microseconds * 10));
                    break;
                case "total_size" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes):
                    totalBytes = bytes;
                    break;
                case "speed":
                    speed = ParseSpeed(value);
                    break;
                case "progress":
                    progress?.Report(new FfmpegProgress(processed, totalBytes, speed, value == "end", value));
                    break;
            }
        }
    }

    private static double? ParseSpeed(string value)
    {
        string normalized = value.EndsWith('x') ? value[..^1] : value;
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static void AppendLimited(StringBuilder target, string line)
    {
        if (target.Length >= MaximumDiagnosticCharacters)
        {
            return;
        }

        int remaining = MaximumDiagnosticCharacters - target.Length;
        target.AppendLine(line.Length <= remaining ? line : line[..remaining]);
    }

    private static void TrySendQuit(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("q");
                process.StandardInput.Flush();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }
}
