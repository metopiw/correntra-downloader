using Avalonia.Threading;
using Correntra.Desktop.ViewModels;
using Correntra.Desktop.Views;
using Velopack;
using Velopack.Sources;

namespace Correntra.Desktop.Services;

public static class GitHubUpdateService
{
    private const string RepositoryUrl = "https://github.com/metopiw/correntra-downloader";

    public static async Task CheckAndOfferAsync(
        MainWindow owner,
        MainViewModel viewModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(viewModel);
        try
        {
            var source = new GithubSource(RepositoryUrl, null, false);
            var manager = new UpdateManager(source);
            if (!manager.IsInstalled)
            {
                return;
            }

            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return;
            }

            bool install = await Dispatcher.UIThread.InvokeAsync(() =>
                new UpdatePromptDialog(update.TargetFullRelease.Version.ToString(), update.TargetFullRelease.NotesMarkdown)
                    .ShowDialog<bool>(owner));
            if (!install)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(
                update,
                progress => Dispatcher.UIThread.Post(() =>
                    viewModel.SetAgentConnection(true, $"Güncelleme indiriliyor: %{progress}")),
                cancellationToken).ConfigureAwait(false);
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    viewModel.SetAgentConnection(viewModel.IsBrowserCaptureConnected, "Güncelleme denetimi şu anda kullanılamıyor."));
            }
        }
    }
}

