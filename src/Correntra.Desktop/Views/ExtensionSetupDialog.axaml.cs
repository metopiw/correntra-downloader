using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Correntra.Desktop.Services;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

/// <summary>
/// Guided "Load unpacked" wizard: prepares the folder, the path and the
/// extensions page, then watches MainViewModel.IsBrowserCaptureConnected so
/// the footer flips to a green "connected" state the moment the extension
/// first reaches the agent.
/// </summary>
public partial class ExtensionSetupDialog : Window
{
    private readonly MainViewModel viewModel;
    private readonly string? extensionFolder;
    private readonly IReadOnlyList<DetectedBrowser> browsers;

    /// <summary>Creates a standalone wizard for XAML runtime loading.</summary>
    public ExtensionSetupDialog()
        : this(new MainViewModel())
    {
    }

    public ExtensionSetupDialog(MainViewModel mainViewModel)
    {
        InitializeComponent();
        viewModel = mainViewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        extensionFolder = ExtensionSetupService.LocateExtensionFolder();
        browsers = ExtensionSetupService.DetectBrowsers();
        ExtensionPathText.Text = extensionFolder ?? LocalizationService.Current["ExtSetup.FolderMissing"];

        string browserNames = browsers.Count == 0
            ? LocalizationService.Current["ExtSetup.NoBrowser"]
            : string.Join(", ", browsers.Select(browser => browser.Name));

        BrowserLineText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.Current["ExtSetup.BrowserLine"],
            browserNames);

        Opened += OnOpened;

        SyncConnectionState(viewModel.IsBrowserCaptureConnected);
    }

    protected override void OnClosed(EventArgs e)
    {
        Opened -= OnOpened;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (extensionFolder is not null)
        {
            ExtensionSetupService.CopyFolderToClipboard(this, extensionFolder);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBrowserCaptureConnected))
        {
            Dispatcher.UIThread.Post(() => SyncConnectionState(viewModel.IsBrowserCaptureConnected));
        }
    }

    private void SyncConnectionState(bool connected)
    {
        StatusDot.Fill = connected
            ? new SolidColorBrush(Color.Parse("#45D39C"))
            : new SolidColorBrush(Color.Parse("#F0B45B"));
        StatusText.Text = connected
            ? LocalizationService.Current["ExtSetup.Connected"]
            : LocalizationService.Current["ExtSetup.Waiting"];
        DoneButton.IsEnabled = true; // The user may also just close and retry later.
    }

    private void OnOpenFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (extensionFolder is not null)
        {
            ExtensionSetupService.OpenExtensionFolder(extensionFolder);
        }
    }

    private void OnCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (extensionFolder is not null)
        {
            ExtensionSetupService.CopyFolderToClipboard(this, extensionFolder);
        }
    }

    private void OnOpenExtensionsPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ExtensionSetupService.OpenExtensionsPage(browsers);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
