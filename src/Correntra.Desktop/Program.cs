using Avalonia;
using System;

using Correntra.Infrastructure.Ipc;
using Velopack;

namespace Correntra.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        if (TryForwardConfirmationToRunningInstance(args))
        {
            // A desktop shell is already running and it will show the modal;
            // this process has nothing left to do.
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static bool TryForwardConfirmationToRunningInstance(string[] args)
    {
        string? jobId = ReadConfirmDownloadJobId(args);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        try
        {
            // Synchronous is acceptable here: this runs before any UI or
            // message loop exists, and the client times out on its own.
            return new DesktopActivationClient().TryConfirmDownloadAsync(jobId)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadConfirmDownloadJobId(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--confirm-download", StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}