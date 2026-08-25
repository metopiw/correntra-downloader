using Avalonia.Controls;
using Correntra.Desktop.Services;

namespace Correntra.Desktop.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        // The version badge must follow the real assembly version, not a
        // translated string that silently falls behind every release.
        string version = GitHubUpdateService.CurrentVersion.ToString(3);
        VersionBadge.Text = "v" + version;
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
