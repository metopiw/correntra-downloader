using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Correntra.Agent.Runtime;

public sealed record YtDlpFormatOption(string Id, string DisplayName, int? Height, bool IsAudioOnly);

public sealed record YtDlpInfo(string Title, IReadOnlyList<YtDlpFormatOption> Options);

public sealed record YtDlpDownloadResult(bool Succeeded, string OutputPath, string Diagnostics);

/// <summary>
/// Social-platform video engine built on the open-source yt-dlp sidecar.
/// YouTube, Facebook, Instagram, X and similar sites serve media through
/// script-generated manifests and rotating segment URLs, so a dedicated
/// extractor is required instead of a plain HTTP download (which would only
/// save a few kilobytes of HTML or manifest text).
/// </summary>
public sealed partial class YtDlpExecutor
{
    /// <summary>
    /// Job header that carries the user-selected yt-dlp format selector. It is
    /// stored with the encrypted request details and never sent to a server.
    /// </summary>
    public const string FormatHeader = "X-Correntra-Format";

    private const int MaximumDiagnosticCharacters = 64 * 1024;
    private static readonly string[] SupportedDomains =
    [
        "youtube.com",
        "youtu.be",
        "youtube-nocookie.com",
        "facebook.com",
        "fb.watch",
        "fb.com",
        "instagram.com",
        "twitter.com",
        "x.com",
        "tiktok.com",
        "twitch.tv",
        "kick.com",
        "vimeo.com",
        "reddit.com",
        "redd.it",
        "dailymotion.com",
        "tumblr.com",
        "soundcloud.com",
        "bandcamp.com",
        "mixcloud.com",
        "vk.com",
        "vk.ru",
        "vkvideo.ru",
        "ok.ru",
        "rutube.ru",
        "rumble.com",
        "bitchute.com",
        "odysee.com",
        "bilibili.com",
        "nicovideo.jp",
        "streamable.com",
        "pinterest.com",
        "linkedin.com",
        "imdb.com",
        "archive.org",
        "aparat.com",
        "9gag.com",
        "pornhub.com",
        "xvideos.com",
        "xnxx.com",
        "xhamster.com",
    ];

    private static readonly string[] FragmentCdnMarkers =
    [
        "googlevideo.com",
        "ytimg.com",
        "ggpht.com",
        "tiktokcdn.com",
        "tiktokv.com",
        "byteoversea.com",
        "ibyteimg.com",
        "fbcdn.net",
        "cdninstagram.com",
        "twimg.com",
        "video.twimg.com",
        "pinimg.com",
        "vumbnail.com",
    ];

    private static readonly HashSet<string> DirectMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".avi",
        ".m4a", ".mp3", ".aac", ".ogg", ".opus", ".flac", ".wav",
        ".m3u8", ".mpd", ".ts",
    };

    private readonly string? _ytDlpPath;

    public YtDlpExecutor(string? ytDlpPath = null)
    {
        _ytDlpPath = ytDlpPath ?? ResolveYtDlpPath();
    }

    public bool IsAvailable => _ytDlpPath is not null;

    public static bool IsSupportedHost(Uri uri)
    {
        string host = uri.Host;
        foreach (string domain in SupportedDomains)
        {
            if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for signed/rotating CDN hosts that look like a file URL but are
    /// only a fragment; the watch-page URL must be extracted instead.
    /// </summary>
    public static bool IsFragmentCdn(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string host = uri.Host;
        foreach (string marker in FragmentCdnMarkers)
        {
            if (string.Equals(host, marker, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the URL path is a standalone progressive file or playlist
    /// that the HTTP/HLS/DASH engines can fetch without a site extractor.
    /// </summary>
    public static bool LooksLikeDirectMedia(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string extension = Path.GetExtension(uri.AbsolutePath);
        return DirectMediaExtensions.Contains(extension);
    }

    /// <summary>
    /// Chooses yt-dlp for known platforms, MSE/blob watch pages, and fragment
    /// CDNs. Direct <c>.mp4</c>/<c>.m3u8</c> files on unrelated hosts stay on
    /// the HTTP engine so a normal file download is not forced through yt-dlp.
    /// Unknown watch pages still try yt-dlp: the sidecar already ships hundreds
    /// of extractors, and a failed probe falls back to manifest resolution.
    /// </summary>
    public static bool ShouldExtractWithYtDlp(Uri source, Uri? pageUrl)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsSupportedHost(source) || (pageUrl is not null && IsSupportedHost(pageUrl)))
        {
            return true;
        }

        if (IsFragmentCdn(source))
        {
            return pageUrl is not null;
        }

        if (LooksLikeDirectMedia(source))
        {
            return false;
        }

        return pageUrl is not null;
    }

    /// <summary>Lists the downloadable qualities for a page or media URL.</summary>
    public async Task<YtDlpInfo> EnumerateFormatsAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string executable = RequireExecutable();

        string json;
        try
        {
            json = await RunCaptureAsync(
                executable,
                BuildEnumerateArguments(url, withCookies: true),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Cookie extraction can fail (browser missing/locked); the
            // anonymous pass still works for most public videos.
            json = await RunCaptureAsync(
                executable,
                BuildEnumerateArguments(url, withCookies: false),
                cancellationToken).ConfigureAwait(false);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string title = root.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString() ?? "media"
            : "media";

        var heights = new SortedSet<int>();
        bool hasAudio = false;
        if (root.TryGetProperty("formats", out JsonElement formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement format in formats.EnumerateArray())
            {
                string? note = ReadString(format, "format_note");
                if (note is not null && note.Contains("storyboard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool hasVideo = ReadString(format, "vcodec") is not (null or "none");
                bool hasAudioTrack = ReadString(format, "acodec") is not (null or "none");
                hasAudio |= hasAudioTrack;
                if (hasVideo &&
                    format.TryGetProperty("height", out JsonElement heightElement) &&
                    heightElement.ValueKind == JsonValueKind.Number &&
                    heightElement.TryGetInt32(out int height) &&
                    height >= 144)
                {
                    heights.Add(height);
                }
            }
        }

        var options = new List<YtDlpFormatOption>();
        foreach (int height in heights.Reverse().Take(6))
        {
            options.Add(new YtDlpFormatOption(
                $"bestvideo[height<={height.ToString(CultureInfo.InvariantCulture)}]+bestaudio/best[height<={height.ToString(CultureInfo.InvariantCulture)}]",
                height >= 2160 ? $"{height}p (4K)" : $"{height}p",
                height,
                IsAudioOnly: false));
        }

        if (hasAudio)
        {
            options.Add(new YtDlpFormatOption("bestaudio/best", "Audio only (best quality)", null, IsAudioOnly: true));
        }

        if (options.Count == 0)
        {
            options.Add(new YtDlpFormatOption("bestvideo+bestaudio/best", "Best available", null, IsAudioOnly: false));
        }

        return new YtDlpInfo(title, options);
    }

    private static List<string> BuildEnumerateArguments(string url, bool withCookies)
    {
        var arguments = new List<string> { "--no-warnings", "-J", "--no-playlist", "--skip-download" };
        if (withCookies)
        {
            // Reusing the browser session defeats bot checks and unlocks
            // age-restricted or members-only uploads.
            arguments.Add("--cookies-from-browser");
            arguments.Add("chrome");
        }

        arguments.Add("--");
        arguments.Add(url);
        return arguments;
    }

    /// <summary>
    /// Downloads the media to <paramref name="outputPath"/>. yt-dlp writes the
    /// file directly (no .part), so interrupted runs can simply be restarted.
    /// </summary>
    public async Task<YtDlpDownloadResult> DownloadAsync(
        string url,
        string? formatSelector,
        string outputPath,
        IReadOnlyDictionary<string, string>? headers = null,
        Action<double, long?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _ = RequireExecutable();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        YtDlpDownloadResult result = await RunDownloadAsync(
            BuildDownloadArguments(url, formatSelector, outputPath, headers, withCookies: true),
            outputPath,
            onProgress,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // Cookie extraction or a cookie-gated path can fail; retry the
            // whole transfer anonymously before reporting a hard failure.
            result = await RunDownloadAsync(
                BuildDownloadArguments(url, formatSelector, outputPath, headers, withCookies: false),
                outputPath,
                onProgress,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static List<string> BuildDownloadArguments(
        string url,
        string? formatSelector,
        string outputPath,
        IReadOnlyDictionary<string, string>? headers,
        bool withCookies)
    {
        var arguments = new List<string>
        {
            "--no-warnings", "--newline", "--no-playlist", "--restrict-filenames",
            "--retries", "10", "--fragment-retries", "10",
            "--no-part", "-o", outputPath,
        };
        if (withCookies)
        {
            arguments.Add("--cookies-from-browser");
            arguments.Add("chrome");
        }

        string? ffmpegLocation = ResolveFfmpegLocation();
        if (ffmpegLocation is not null)
        {
            // Merging separate video/audio tracks (bestvideo+bestaudio) needs an
            // FFmpeg sidecar; point yt-dlp at the one shipped with the app.
            arguments.Add("--ffmpeg-location");
            arguments.Add(ffmpegLocation);

            // A merge can pick a container that differs from the requested
            // extension (webm vs mp4); remux so the final file matches.
            string destinationExtension = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
            if (destinationExtension is "mp4" or "mkv" or "mov" or "webm")
            {
                arguments.Add("--remux-video");
                arguments.Add(destinationExtension);
            }
        }

        if (!string.IsNullOrWhiteSpace(formatSelector))
        {
            arguments.Add("-f");
            arguments.Add(formatSelector);
        }

        // Session cookies forwarded by the extension defeat bot checks on
        // YouTube/Facebook/Instagram; only identity headers are relayed.
        if (headers is not null)
        {
            foreach (string name in new[] { "Cookie", "User-Agent", "Referer" })
            {
                if (headers.TryGetValue(name, out string? value) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    !value.Contains('\r') && !value.Contains('\n'))
                {
                    arguments.Add("--add-header");
                    arguments.Add($"{name}: {value}");
                }
            }
        }

        arguments.Add("--");
        arguments.Add(url);
        return arguments;
    }

    private async Task<YtDlpDownloadResult> RunDownloadAsync(
        List<string> arguments,
        string outputPath,
        Action<double, long?>? onProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RequireExecutable(),
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

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("The yt-dlp process could not be started.");
        }

        var diagnostic = new StringBuilder();
        Task outputDrain = DrainLinesAsync(process.StandardOutput, diagnostic, onProgress, cancellationToken);
        Task errorDrain = DrainLinesAsync(process.StandardError, diagnostic, null, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return new YtDlpDownloadResult(process.ExitCode == 0, outputPath, diagnostic.ToString());
    }

    private static async Task<string> RunCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
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

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("The yt-dlp process could not be started.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                "yt-dlp could not read this media: " + detail.Trim().Split('\n').LastOrDefault());
        }

        return output;
    }

    private static async Task DrainLinesAsync(
        StreamReader reader,
        StringBuilder diagnostic,
        Action<double, long?>? onProgress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            AppendLimited(diagnostic, line);
            if (onProgress is null)
            {
                continue;
            }

            Match match = ProgressLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            double percent = double.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture);
            long? estimatedTotal = ParseSize(match.Groups["size"].Value, match.Groups["unit"].Value);
            onProgress(Math.Clamp(percent, 0, 100), estimatedTotal);
        }
    }

    private static long? ParseSize(string size, string unit)
    {
        if (string.IsNullOrEmpty(size) || string.IsNullOrEmpty(unit))
        {
            return null;
        }

        if (!double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return null;
        }

        double multiplier = unit.ToUpperInvariant()[0] switch
        {
            'K' => 1024,
            'M' => 1024 * 1024,
            'G' => 1024L * 1024 * 1024,
            _ => 1,
        };
        return (long)(value * multiplier);
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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string RequireExecutable() =>
        _ytDlpPath ?? throw new InvalidOperationException(
            "yt-dlp.exe was not found. Run scripts\\get-yt-dlp.ps1 or place yt-dlp.exe next to the application.");

    private static string? ResolveFfmpegLocation()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        foreach (string candidate in new[]
                 {
                     Path.Combine(baseDirectory, "ffmpeg.exe"),
                     Path.Combine(baseDirectory, "vendor", "ffmpeg.exe"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveYtDlpPath()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(baseDirectory, "yt-dlp.exe"),
                         Path.Combine(baseDirectory, "vendor", "yt-dlp.exe"),
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), "yt-dlp.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not break engine discovery.
                }
            }
        }

        return null;
    }

    [GeneratedRegex(
        @"\[download\]\s+(?<pct>\d{1,3}(?:\.\d+)?)%(?:\s+of\s+~?\s*(?<size>\d+(?:\.\d+)?)(?<unit>[KMGkMGT]i?B))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();
}
