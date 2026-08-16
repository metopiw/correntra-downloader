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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            LocalizationService.Current.InitializeFromSystemCulture();
            var viewModel = new MainViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;
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
        Observe(GitHubUpdateService.CheckAndOfferAsync(mainWindow, viewModel), "GitHub update check");
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
