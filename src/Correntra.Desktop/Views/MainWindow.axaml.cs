using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Correntra.Core.Ipc;
using Correntra.Desktop.Models;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel? subscribedViewModel;

    /// <summary>Ring buffer of recent aggregate speeds (bytes/s) for the sparkline.</summary>
    private readonly double[] speedHistory = new double[60];

    private int speedHistoryCount;

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
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (subscribedViewModel is { } viewModel)
        {
            viewModel.DialogRequested -= OnDialogRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Redraws the 150x22 sparkline only when the aggregate speed text
    /// changes (≈ twice a second at most). Sixty polyline points and one
    /// Text element per redraw — negligible CPU/RAM, no timers of its own.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.AggregateSpeedText) ||
            DataContext is not MainViewModel viewModel ||
            this.FindControl<Canvas>("SpeedGraph") is not { } canvas)
        {
            return;
        }

        speedHistory[speedHistoryCount % speedHistory.Length] = viewModel.AggregateBytesPerSecond;
        speedHistoryCount++;
        if (speedHistoryCount < 3)
        {
            return; // Need at least two samples to draw a line.
        }

        double peak = 1;
        for (int index = 0; index < speedHistory.Length && index < speedHistoryCount; index++)
        {
            peak = Math.Max(peak, speedHistory[index]);
        }

        int written = Math.Min(speedHistoryCount, speedHistory.Length);
        var points = new List<Point>(written);
        for (int step = 0; step < written; step++)
        {
            // Oldest sample at the left edge; the ring wraps naturally.
            int index = speedHistoryCount > speedHistory.Length
                ? (speedHistoryCount + step) % speedHistory.Length
                : step;
            double x = canvas.Width * step / (written - 1);
            double y = canvas.Height - 2 - (canvas.Height - 4) * (speedHistory[index] / peak);
            points.Add(new Point(x, y));
        }

        SpeedGraphLine.Points = points;
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
        // A capture can arrive while the shell sits minimized or hidden in the
        // tray; a modal owned by an invisible window would never be seen.
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
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
