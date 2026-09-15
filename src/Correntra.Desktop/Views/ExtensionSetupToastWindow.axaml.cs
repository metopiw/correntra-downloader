using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Correntra.Desktop.ViewModels;

namespace Correntra.Desktop.Views;

/// <summary>
/// Non-modal bottom-right reminder shown while the genuine browser extension
/// has not contacted the agent. Choosing setup opens the existing guided
/// dialog; a late extension heartbeat dismisses the reminder automatically.
/// </summary>
public partial class ExtensionSetupToastWindow : Window
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MainViewModel? viewModel;

    public ExtensionSetupToastWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    public static Task<bool> ShowPrompt(Window owner, MainViewModel mainViewModel)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mainViewModel);

        var toast = new ExtensionSetupToastWindow
        {
            viewModel = mainViewModel,
        };
        mainViewModel.PropertyChanged += toast.OnViewModelPropertyChanged;
        toast.Opened += (_, _) => toast.DockToBottomRight(owner);
        toast.Show(owner);
        return toast.completion.Task;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBrowserCaptureConnected) &&
            viewModel?.IsBrowserCaptureConnected == true)
        {
            Dispatcher.UIThread.Post(Close);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel = null;
        }

        completion.TrySetResult(false);
    }

    private void DockToBottomRight(Window owner)
    {
        try
        {
            Screen? screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            if (screen is null)
            {
                return;
            }

            PixelRect area = screen.WorkingArea;
            double scaling = RenderScaling;
            int width = (int)(Bounds.Width * scaling);
            int height = (int)(Bounds.Height * scaling);
            Position = new PixelPoint(area.Right - width - 12, area.Bottom - height - 12);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Extension setup toast positioning failed: {exception.Message}");
        }
    }

    private void OnSetupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        completion.TrySetResult(true);
        Close();
    }

    private void OnLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
