using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Correntra.Desktop.Services;

/// <summary>Outcome of a user-initiated update of the bundled yt-dlp sidecar.</summary>
public enum YtDlpUpdateState
{
    Updated,
    AlreadyCurrent,
    NotBundled,
    CouldNotCheck,
    CouldNotReplace,
}

/// <summary>Details returned after checking the bundled media extractor.</summary>
public sealed record YtDlpUpdateResult(YtDlpUpdateState State, string? Version = null);

/// <summary>
/// Updates only the yt-dlp executable shipped beside Correntra.  It deliberately
/// does not touch a copy found on PATH: that may belong to another application.
/// </summary>
public static class YtDlpUpdateService
{
    private const string NightlyReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest";
    private const string StableReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const long MaximumDownloadBytes = 100L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Gets the newest nightly build, falling back to stable when nightly is
    /// unavailable, verifies that it runs, then replaces the bundled sidecar.
    /// </summary>
    public static async Task<YtDlpUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        string? executablePath = FindBundledExecutable();
        if (executablePath is null)
        {
            return new YtDlpUpdateResult(YtDlpUpdateState.NotBundled);
        }

        YtDlpRelease? release;
        try
        {
            release = await QueryLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            Trace.WriteLine($"yt-dlp update check failed: {exception.Message}");
            return new YtDlpUpdateResult(YtDlpUpdateState.CouldNotCheck);
        }

        if (release is null || string.IsNullOrWhiteSpace(release.AssetUrl))
        {
            return new YtDlpUpdateResult(YtDlpUpdateState.CouldNotCheck);
        }

        string? installedVersion = await ReadVersionAsync(executablePath, cancellationToken).ConfigureAwait(false);
        if (string.Equals(installedVersion, release.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new YtDlpUpdateResult(YtDlpUpdateState.AlreadyCurrent, installedVersion);
        }

        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(executablePath)!,
            $"yt-dlp.{Guid.NewGuid():N}.download");
        try
        {
            await DownloadAsync(release.AssetUrl, temporaryPath, cancellationToken).ConfigureAwait(false);
            string? downloadedVersion = await ReadVersionAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloadedVersion))
            {
                return new YtDlpUpdateResult(YtDlpUpdateState.CouldNotCheck);
            }

            try
            {
                File.Move(temporaryPath, executablePath, overwrite: true);
            }
            catch (IOException exception)
            {
                // A live yt-dlp process keeps the executable locked on Windows.
                Trace.WriteLine($"yt-dlp could not be replaced: {exception.Message}");
                return new YtDlpUpdateResult(YtDlpUpdateState.CouldNotReplace);
            }

            return new YtDlpUpdateResult(YtDlpUpdateState.Updated, downloadedVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            Trace.WriteLine($"yt-dlp download failed: {exception.Message}");
            return new YtDlpUpdateResult(YtDlpUpdateState.CouldNotCheck);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The failed update does not affect downloads; leave cleanup to a later run.
            }
        }
    }

    private static string? FindBundledExecutable()
    {
        string baseDirectory = AppContext.BaseDirectory;
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

        return null;
    }

    private static async Task<YtDlpRelease?> QueryLatestReleaseAsync(CancellationToken cancellationToken)
    {
        foreach (string apiUrl in new[] { NightlyReleaseApiUrl, StableReleaseApiUrl })
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                YtDlpReleaseDto? dto = await JsonSerializer.DeserializeAsync<YtDlpReleaseDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                string? assetUrl = dto?.Assets?.FirstOrDefault(static asset =>
                    string.Equals(asset.Name, "yt-dlp.exe", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;
                if (!string.IsNullOrWhiteSpace(dto?.TagName) && !string.IsNullOrWhiteSpace(assetUrl))
                {
                    return new YtDlpRelease(dto.TagName, assetUrl);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
            {
                Trace.WriteLine($"yt-dlp release endpoint failed ({apiUrl}): {exception.Message}");
            }
        }

        return null;
    }

    private static async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            throw new IOException("The yt-dlp download exceeded the expected size.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > MaximumDownloadBytes)
            {
                throw new IOException("The yt-dlp download exceeded the expected size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
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

            return null;
        }

        return process.ExitCode == 0 ? (await output.ConfigureAwait(false)).Trim() : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Correntra-Downloader");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record YtDlpRelease(string Version, string AssetUrl);

    private sealed record YtDlpReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("assets")] YtDlpAssetDto[]? Assets);

    private sealed record YtDlpAssetDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
