using Correntra.Media.Processing;

namespace Correntra.Agent.Runtime;

/// <summary>
/// Downloads HLS and DASH manifests by handing them to a verified LGPL FFmpeg
/// sidecar for remuxing, instead of saving the manifest document itself as if
/// it were a regular file.
/// </summary>
public sealed class MediaExecutor
{
    private readonly string? _ffmpegPath;
    private FfmpegInspectionResult? _inspection;
    private bool _inspectionDone;

    public MediaExecutor(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? ResolveFfmpegPath();
    }

    public bool IsAvailable => _ffmpegPath is not null;

    public async Task<FfmpegInspectionResult> GetInspectionAsync(CancellationToken cancellationToken = default)
    {
        if (_ffmpegPath is null)
        {
            return new(false, false, string.Empty, string.Empty, string.Empty, "FFmpeg executable was not found.");
        }

        if (!_inspectionDone)
        {
            _inspection = await FfmpegInspector.InspectAsync(_ffmpegPath, cancellationToken).ConfigureAwait(false);
            _inspectionDone = true;
        }

        return _inspection!;
    }

    public async Task<FfmpegRunResult> RemuxAsync(
        string sourceUrl,
        string outputPath,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        FfmpegInspectionResult inspection = await GetInspectionAsync(cancellationToken).ConfigureAwait(false);
        if (!inspection.IsLgplCompatible)
        {
            throw new InvalidOperationException(
                "FFmpeg unavailable or not LGPL-compatible: " + (inspection.FailureReason ?? "unknown reason"));
        }

        string container = Path.GetExtension(new Uri(sourceUrl).AbsolutePath).TrimStart('.');
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-progress", "pipe:2",
        };

        string? headerArgument = BuildHeaderArgument(headers);
        if (headerArgument is not null)
        {
            // -headers is a top-level passthrough for HTTP(S) inputs.
            arguments.Add("-headers");
            arguments.Add(headerArgument);
        }

        string outputExtension = container is "m3u8" or "mpd" ? "mp4" : (container.Length > 0 ? container : "mp4");
        string finalPath = Path.ChangeExtension(outputPath, "." + outputExtension);
        File.Delete(finalPath);

        arguments.Add("-i");
        arguments.Add(sourceUrl);
        arguments.Add("-map");
        arguments.Add("0");
        arguments.Add("-c");
        arguments.Add("copy");
        arguments.Add("-movflags");
        arguments.Add("+faststart");
        arguments.Add(finalPath);

        FfmpegRunResult result = await FfmpegRunner.RunAsync(
            _ffmpegPath!,
            arguments,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded && !string.Equals(finalPath, outputPath, StringComparison.Ordinal))
        {
            File.Move(finalPath, outputPath, overwrite: true);
        }

        return result;
    }

    private static string? BuildHeaderArgument(IReadOnlyDictionary<string, string> headers)
    {
        var lines = new List<string>();
        foreach ((string name, string value) in headers)
        {
            if (IsManagedHeader(name))
            {
                continue;
            }

            // Keep values free of CR/LF so the argument list stays single-line.
            string safeName = name.Replace('\r', ' ').Replace('\n', ' ');
            string safeValue = value.Replace('\r', ' ').Replace('\n', ' ');
            lines.Add($"{safeName}: {safeValue}");
        }

        return lines.Count > 0 ? string.Join("\r\n", lines) : null;
    }

    private static bool IsManagedHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveFfmpegPath()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "ffmpeg.exe"),
            Path.Combine(baseDirectory, "vendor", "ffmpeg.exe"),
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
