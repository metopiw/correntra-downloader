using Avalonia;
using Avalonia.Controls;
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
    }

    private async void OnOpenSchedulerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await new QueueScheduleDialog().ShowDialog(this).ConfigureAwait(true);

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = viewModel.SelectedTheme.Value == "Light"
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }

        LocalizationService.Current.SetLanguage(viewModel.SelectedLanguage.Value);
        Close(true);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
