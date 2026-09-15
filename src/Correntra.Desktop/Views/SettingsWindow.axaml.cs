using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Correntra.Desktop.Services;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel viewModel = new();

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        CurrentVersionText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.Current["Update.Version"],
            GitHubUpdateService.CurrentVersion.ToString(3));
    }

    private async void OnOpenSchedulerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await new QueueScheduleDialog().ShowDialog(this).ConfigureAwait(true);

    private async void OnCheckUpdatesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            UpdateStatusText.Text = LocalizationService.Current["Settings.Updates.Checking"];
            YtDlpUpdateResult ytDlp = await YtDlpUpdateService.UpdateAsync().ConfigureAwait(true);
            MainViewModel mainViewModel = App.CurrentMainWindow?.DataContext as MainViewModel ?? new MainViewModel();
            string appStatus = await GitHubUpdateService.CheckAndOfferAsync(
                this,
                mainViewModel,
                viewModel.IncludePrereleases).ConfigureAwait(true);
            UpdateStatusText.Text = FormatYtDlpUpdateStatus(ytDlp) + Environment.NewLine + appStatus;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private static string FormatYtDlpUpdateStatus(YtDlpUpdateResult result)
    {
        string key = result.State switch
        {
            YtDlpUpdateState.Updated => "Settings.Updates.YtDlp.Updated",
            YtDlpUpdateState.AlreadyCurrent => "Settings.Updates.YtDlp.Current",
            YtDlpUpdateState.NotBundled => "Settings.Updates.YtDlp.NotBundled",
            YtDlpUpdateState.CouldNotReplace => "Settings.Updates.YtDlp.Busy",
            _ => "Settings.Updates.YtDlp.Failed",
        };
        return result.Version is null
            ? LocalizationService.Current[key]
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationService.Current[key], result.Version);
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = viewModel.SelectedTheme.Value == "Light"
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }

        LocalizationService.Current.SetLanguage(viewModel.SelectedLanguage.Value);
        viewModel.Save();
        Close(true);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private async void OnExtensionSetupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainViewModel mainViewModel)
        {
            await new ExtensionSetupDialog(mainViewModel).ShowDialog(this);
        }
        else
        {
            await new ExtensionSetupDialog(new MainViewModel()).ShowDialog(this);
        }
    }

    private void OnVirusTotalKeyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.virustotal.com/gui/my-apikey") { UseShellExecute = true });
        }
        catch
        {
            // Browser launch is best-effort; the URL is also shown in the docs.
        }
    }
}
