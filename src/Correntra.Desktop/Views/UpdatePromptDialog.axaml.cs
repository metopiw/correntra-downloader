using Avalonia.Controls;
using Correntra.Desktop.Services;

namespace Correntra.Desktop.Views;

public partial class UpdatePromptDialog : Window
{
    public UpdatePromptDialog()
        : this("0.1.0", string.Empty)
    {
    }

    public UpdatePromptDialog(string version, string? notes)
    {
        InitializeComponent();
        LocalizationService localizer = LocalizationService.Current;
        HeadingText.Text = localizer["Update.Title"];
        VersionText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            localizer["Update.Version"],
            version);
        NotesText.Text = string.IsNullOrWhiteSpace(notes) ? localizer["Update.NoNotes"] : notes;
        LaterButton.Content = localizer["Update.Later"];
        InstallButton.Content = localizer["Update.Install"];
    }

    private void OnLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void OnInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);
}

