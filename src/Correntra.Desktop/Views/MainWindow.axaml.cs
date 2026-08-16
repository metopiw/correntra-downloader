using Avalonia.Controls;
using Avalonia.Input;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;
using Correntra.Core.Ipc;

namespace Correntra.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel? subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainViewModel viewModel)
        {
            subscribedViewModel = viewModel;
            viewModel.DialogRequested += OnDialogRequested;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (subscribedViewModel is { } viewModel)
        {
            viewModel.DialogRequested -= OnDialogRequested;
        }

        base.OnClosed(e);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Do not start a move drag when the press originates from a window
        // control button (minimize/maximize/close); otherwise the drag steals
        // the click and the button never receives its event.
        if (e.Source is Button)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizedState();
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ToggleMaximizedState();

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnDownloadListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-clicking a row opens the finished file, matching the
        // right-click "Open file" action users already know.
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OpenFileCommand.Execute(null);
        }
    }

    private void OnExitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async void OnDialogRequested(object? sender, DialogRequestEventArgs e)
    {
        switch (e.Kind)
        {
            case DesktopDialogKind.AddUrl:
                await ShowAddUrlFlowAsync().ConfigureAwait(true);
                break;
            case DesktopDialogKind.MediaSelection:
                await new MediaSelectionDialog().ShowDialog<MediaSelectionResult?>(this).ConfigureAwait(true);
                break;
            case DesktopDialogKind.Settings:
                await new SettingsWindow().ShowDialog<bool>(this).ConfigureAwait(true);
                break;
            case DesktopDialogKind.Scheduler:
                await new QueueScheduleDialog().ShowDialog<QueueScheduleResult?>(this).ConfigureAwait(true);
                break;
            case DesktopDialogKind.About:
                await new AboutWindow().ShowDialog(this).ConfigureAwait(true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e));
        }
    }

    private async Task ShowAddUrlFlowAsync()
    {
        var addResult = await new AddUrlDialog().ShowDialog<AddUrlResult?>(this).ConfigureAwait(true);
        if (addResult is null)
        {
            return;
        }

        var confirmation = await new DownloadConfirmationDialog(addResult.Url)
            .ShowDialog<DownloadConfirmationResult?>(this)
            .ConfigureAwait(true);
        if (confirmation is not null && confirmation.Action != DownloadConfirmationAction.Cancel)
        {
            subscribedViewModel?.SubmitDownload(confirmation);
        }
    }

    public Task<DownloadConfirmationResult?> ShowPendingDownloadConfirmationAsync(DownloadJobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Activate();
        return new DownloadConfirmationDialog(
            job.SourceDisplayUri,
            job.FileName,
            job.DestinationDirectory).ShowDialog<DownloadConfirmationResult?>(this);
    }

    private void ToggleMaximizedState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
