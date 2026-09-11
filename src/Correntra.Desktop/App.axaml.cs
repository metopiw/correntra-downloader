using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Correntra.Desktop.Services;
using Correntra.Desktop.ViewModels;
using Correntra.Desktop.Views;

namespace Correntra.Desktop;

public partial class App : Application, IDisposable
{
    private DesktopAgentBridge? agentBridge;
    private bool _extensionWizardShownThisLaunch;

    /// <summary>Shared access for dialogs (settings update check) that need the
    /// live main view model but are not created through the lifetime callback.</summary>
    internal static MainWindow? CurrentMainWindow { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            LocalizationService.Current.InitializeFromSettings();
            var viewModel = new MainViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;
            CurrentMainWindow = mainWindow;
            if (!Design.IsDesignMode)
            {
                agentBridge = new DesktopAgentBridge(
                    viewModel,
                    mainWindow,
                    Environment.GetCommandLineArgs().Skip(1));
                mainWindow.Opened += OnMainWindowOpened;
                desktop.ShutdownRequested += (_, _) => Dispose();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose()
    {
        DesktopAgentBridge? bridge = agentBridge;
        agentBridge = null;
        if (bridge is not null)
        {
            bridge.Stop();
            Observe(bridge.DisposeAsync().AsTask(), "Desktop Agent bridge cleanup");
        }

        GC.SuppressFinalize(this);
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (agentBridge is not { } bridge || sender is not MainWindow mainWindow ||
            mainWindow.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Observe(bridge.StartAsync(), "Desktop Agent bridge startup");
        Observe(GitHubUpdateService.RunStartupCheckAsync(mainWindow, viewModel), "GitHub update check");

        // First-run extension wizard. The old "shown once, never again"
        // setting was wrong twice over: it survived reinstalls (so a fresh
        // Setup never showed the wizard) and it gated on the desktop↔agent
        // pipe instead of the actual extension. The viewModel's
        // IsBrowserCaptureConnected is now driven by the agent's real
        // extension-activity clock (MainViewModel.ApplyAgentSnapshot), so we
        // evaluate state rather than waiting for a change: the first snapshot
        // arrives via polling even when the property never transitions.
        // Until the extension is verified, show the wizard once per launch.
        Observe(WaitForExtensionOrShowWizardAsync(viewModel), "Extension setup wizard");
    }

    private async Task WaitForExtensionOrShowWizardAsync(MainViewModel viewModel)
    {
        // Give the bridge a moment to reach the agent and pull the first
        // snapshot; the wizard decision then rests on real data.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (viewModel.IsBrowserCaptureConnected)
            {
                return; // Extension verified — nothing to show.
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        // ~10 s without any verified extension contact and the shipped
        // extension folder exists → run the wizard once this launch.
        if (viewModel.IsBrowserCaptureConnected ||
            ExtensionSetupService.LocateExtensionFolder() is null ||
            _extensionWizardShownThisLaunch)
        {
            return;
        }

        _extensionWizardShownThisLaunch = true;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (CurrentMainWindow is not null)
            {
                await new ExtensionSetupDialog(viewModel).ShowDialog(CurrentMainWindow);
            }
        });
    }

    private static void Observe(Task task, string operation)
    {
        _ = task.ContinueWith(
            completed => Trace.WriteLine($"{operation} failed: {completed.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
