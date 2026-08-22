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
}
