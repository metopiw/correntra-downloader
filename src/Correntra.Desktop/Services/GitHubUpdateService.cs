using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Threading;
using Correntra.Desktop.ViewModels;
using Correntra.Desktop.Views;
using Velopack;
using Velopack.Sources;

namespace Correntra.Desktop.Services;

public sealed record GitHubRelease(string TagName, string DisplayName, bool IsPrerelease)
{
    /// <summary>Parses tags like "v0.3.1"; returns null when the tag is not a version.</summary>
    public Version? TryGetVersion() =>
        Version.TryParse(TagName.TrimStart('v', 'V'), out Version? version) ? version : null;
}

/// <summary>
/// GitHub release checks that work in every run mode. The Velopack engine
/// only answers when the app is setup-installed; portable/dev runs fall back
/// to the GitHub REST API and — when the repository is private and the API
/// answers 404 — to the locally authenticated <c>gh</c> CLI (its credentials
/// never enter this process's memory or logs).
/// </summary>
public static class GitHubUpdateService
{
    private const string RepositoryUrl = "https://github.com/metopiw/correntra-downloader";
    private const string ReleasesApiUrl = "https://api.github.com/repos/metopiw/correntra-downloader/releases";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Startup hook: honours the persisted "check at startup" switch.</summary>
    public static Task RunStartupCheckAsync(Window owner, MainViewModel viewModel, CancellationToken cancellationToken = default)
    {
        DesktopSettings settings = DesktopSettingsStore.Load();
        if (!settings.CheckUpdatesAtStartup)
        {
            return Task.CompletedTask;
        }

        return CheckAndOfferAsync(owner, viewModel, settings.IncludePrereleases, cancellationToken);
    }

    /// <summary>Checks for a newer release and offers it bottom-right. Returns a status message.</summary>
    public static async Task<string> CheckAndOfferAsync(
        Window owner,
        MainViewModel viewModel,
        bool includePrereleases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(viewModel);
        GitHubRelease? latest = await QueryLatestReleaseAsync(includePrereleases, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return "Güncelleme denetimi şu anda kullanılamıyor.";
        }

        Version? candidate = latest.TryGetVersion();
        if (candidate is null || candidate <= CurrentVersion)
        {
            return string.Format(CultureInfo.CurrentCulture, "En güncel sürümü kullanıyorsun (v{0}).", CurrentVersion);
        }

        bool install = await Dispatcher.UIThread.InvokeAsync(
            () => UpdateToastWindow.ShowPrompt(owner, "v" + candidate.ToString(3), latest.DisplayName)).ConfigureAwait(true);
        if (!install)
        {
            return string.Format(CultureInfo.CurrentCulture, "v{0} güncellemesi ertelendi.", candidate);
        }

        await InstallAsync(viewModel, latest, cancellationToken).ConfigureAwait(false);
        return string.Format(CultureInfo.CurrentCulture, "v{0} güncellemesi kuruluyor…", candidate);
    }

    private static async Task InstallAsync(MainViewModel viewModel, GitHubRelease release, CancellationToken cancellationToken)
    {
        var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
        if (manager.IsInstalled)
        {
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                OpenReleasesPage();
                return;
            }

            await manager.DownloadUpdatesAsync(
                update,
                progress => Dispatcher.UIThread.Post(() =>
                    viewModel?.SetAgentConnection(true, $"Güncelleme indiriliyor: %{progress}")),
                cancellationToken).ConfigureAwait(false);
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            return;
        }

        // Portable/dev runs cannot self-replace; the release page carries the
        // current package. Opening it keeps the promise without faking an install.
        OpenReleasesPage();
    }

    private static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl + "/releases") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"Could not open the releases page: {exception.Message}");
        }
    }

    private static async Task<GitHubRelease?> QueryLatestReleaseAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryViaHttpAsync(includePrereleases, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or TaskCanceledException or IOException)
        {
            Trace.WriteLine($"Anonymous GitHub query failed ({exception.Message}); trying the gh CLI.");
        }

        return await QueryViaGhCliAsync(includePrereleases, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubRelease?> QueryViaHttpAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(
            ReleasesApiUrl + "?per_page=10",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return PickRelease(await JsonSerializer.DeserializeAsync<GitHubReleaseDto[]>(stream, JsonOptions, cancellationToken).ConfigureAwait(false), includePrereleases);
    }

    /// <summary>
    /// Fallback for private repositories: the user's installed gh CLI already
    /// holds auth; we only read its stdout, never its token.
    /// </summary>
    private static async Task<GitHubRelease?> QueryViaGhCliAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            ArgumentList =
            {
                "api",
                "repos/metopiw/correntra-downloader/releases",
                "-q", ".",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
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
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
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
            catch (System.InvalidOperationException)
            {
            }

            return null;
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        _ = errors;
        GitHubReleaseDto[]? releases = JsonSerializer.Deserialize<GitHubReleaseDto[]>(await output.ConfigureAwait(false), JsonOptions);
        return PickRelease(releases, includePrereleases);
    }

    private static GitHubRelease? PickRelease(GitHubReleaseDto[]? releases, bool includePrereleases)
    {
        foreach (GitHubReleaseDto dto in releases ?? [])
        {
            if (dto.Draft || (!includePrereleases && dto.Prerelease))
            {
                continue;
            }

            return new GitHubRelease(dto.TagName ?? string.Empty, dto.Name ?? string.Empty, dto.Prerelease);
        }

        return null;
    }

    private static Version ReadCurrentVersion()
    {
        string informational = typeof(GitHubUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        string numeric = informational.Split('+')[0];
        return Version.TryParse(numeric, out Version? version) ? version : new Version(0, 0, 0);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Correntra-Downloader");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
