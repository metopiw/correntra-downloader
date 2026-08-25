using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System.Diagnostics;
using Correntra.Desktop.Services;
using Correntra.Desktop.ViewModels;
using Correntra.Desktop.Views;

namespace Correntra.Desktop;

public partial class App : Application, IDisposable
{
    private DesktopAgentBridge? agentBridge;

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

        // First-run extension wizard: only when capture is not already live
        // (a returning user with the extension installed never sees it).
        if (!viewModel.IsBrowserCaptureConnected)
        {
            DesktopSettings settings = DesktopSettingsStore.Load();
            if (!settings.ExtensionSetupShown &&
                ExtensionSetupService.LocateExtensionFolder() is not null)
            {
                settings.ExtensionSetupShown = true;
                DesktopSettingsStore.Save(settings);
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    await new ExtensionSetupDialog(viewModel).ShowDialog(mainWindow));
            }
        }
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
