using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

public partial class DownloadConfirmationDialog : Window
{
    private readonly DownloadConfirmationViewModel viewModel;

    public DownloadConfirmationDialog()
        : this("https://example.com/download.bin")
    {
    }

    public DownloadConfirmationDialog(string sourceUrl)
        : this(sourceUrl, null, null)
    {
    }

    public DownloadConfirmationDialog(
        string sourceUrl,
        string? suggestedFileName,
        string? suggestedDestination)
    {
        InitializeComponent();
        viewModel = new DownloadConfirmationViewModel(sourceUrl, suggestedFileName, suggestedDestination);
        DataContext = viewModel;
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = Correntra.Desktop.Services.LocalizationService.Current["Dialog.Confirm.Destination"],
        }).ConfigureAwait(true);

        if (folders.Count > 0)
        {
            viewModel.Destination = folders[0].Path.LocalPath;
        }
    }

    private void OnNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Complete(DownloadConfirmationAction.DownloadNow);

    private void OnLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Complete(DownloadConfirmationAction.DownloadLater);

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private void Complete(DownloadConfirmationAction action)
    {
        Close(new DownloadConfirmationResult(
            action,
            viewModel.Url,
            viewModel.FileName,
            viewModel.SelectedCategory.Value,
            viewModel.Destination,
            viewModel.RememberForSite));
    }
}
