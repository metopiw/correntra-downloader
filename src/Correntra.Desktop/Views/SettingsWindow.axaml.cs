using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using System.Diagnostics;
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
        UpdateStatusText.Text = LocalizationService.Current["Settings.Updates.Checking"];
        MainViewModel mainViewModel = App.CurrentMainWindow?.DataContext as MainViewModel ?? new MainViewModel();
        string status = await GitHubUpdateService.CheckAndOfferAsync(
            this,
            mainViewModel,
            viewModel.IncludePrereleases).ConfigureAwait(true);
        UpdateStatusText.Text = status;
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
