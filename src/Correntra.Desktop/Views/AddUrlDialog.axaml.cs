using Avalonia.Controls;
using Avalonia.Input.Platform;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

public partial class AddUrlDialog : Window
{
    private readonly AddUrlDialogViewModel viewModel = new();

    public AddUrlDialog()
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => UrlTextBox.Focus();
    }

    private async void OnPasteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            viewModel.Url = (await clipboard.TryGetTextAsync().ConfigureAwait(true))?.Trim() ?? string.Empty;
        }
    }

    private void OnAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        viewModel.ShowValidationError = true;
        if (AddUrlDialogViewModel.IsValidUrl(viewModel.Url))
        {
            Close(new AddUrlResult(viewModel.Url.Trim()));
        }
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(null);
}
