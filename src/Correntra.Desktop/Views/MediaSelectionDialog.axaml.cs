using Avalonia.Controls;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

public partial class MediaSelectionDialog : Window
{
    private readonly MediaSelectionViewModel viewModel = new();

    public MediaSelectionDialog()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDownloadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(new MediaSelectionResult(
            viewModel.SelectedVideoTrack,
            viewModel.SelectedAudioTrack,
            viewModel.SelectedFormat,
            viewModel.SelectedSubtitle,
            viewModel.AudioOnly,
            viewModel.EmbedMetadata));
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}
