using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Correntra.Desktop.Services;

namespace Correntra.Desktop.Views;

/// <summary>
/// Small non-modal prompt docked to the bottom-right of the work area — the
/// IDM-style "a new version is available" bubble. It must not steal focus on
/// open (ShowActivated=false) and never blocks the main window.
/// </summary>
public partial class UpdateToastWindow : Window
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private UpdateToastWindow()
    {
        InitializeComponent();
        LocalizationService localizer = LocalizationService.Current;
        LaterButton.Content = localizer["Update.Later"];
        InstallButton.Content = localizer["Update.Install"];
        Closed += (_, _) => completion.TrySetResult(false);
    }

    public static Task<bool> ShowPrompt(Window owner, string version, string releaseName)
    {
        LocalizationService localizer = LocalizationService.Current;
        var toast = new UpdateToastWindow();
        toast.HeadingText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            localizer["Update.Toast.Heading"],
            version);
        toast.BodyText.Text = string.IsNullOrWhiteSpace(releaseName)
            ? localizer["Update.Toast.Body"]
            : releaseName;
        toast.Opened += (_, _) => toast.DockToBottomRight(owner);
        toast.Show(owner);
        return toast.completion.Task;
    }

    /// <summary>Anchors the toast inside the owner's screen work area.</summary>
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
            Trace.WriteLine($"Update toast positioning failed: {exception.Message}");
        }
    }

    private void OnInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        completion.TrySetResult(true);
        Close();
    }

    private void OnLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        completion.TrySetResult(false);
        Close();
    }
}
