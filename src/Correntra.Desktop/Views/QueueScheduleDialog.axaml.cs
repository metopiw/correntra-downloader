using Avalonia.Controls;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

public partial class QueueScheduleDialog : Window
{
    private readonly QueueScheduleViewModel viewModel = new();

    public QueueScheduleDialog()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(new QueueScheduleResult(
            viewModel.IsEnabled,
            viewModel.StartTime,
            viewModel.StopTime,
            viewModel.ConcurrentDownloads,
            viewModel.SpeedLimitKilobytes,
            viewModel.SelectedCompletionAction.Value));
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}
