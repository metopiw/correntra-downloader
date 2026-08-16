using Correntra.Agent.Runtime;
using Correntra.Infrastructure.Ipc;
using Correntra.Infrastructure.Storage;
using Correntra.Transfer;

return await AgentProgram.RunAsync(args).ConfigureAwait(false);

internal static class AgentProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? dataRoot = ReadOption(args, "--data-root");
        bool runOnce = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        bool health = args.Contains("--health", StringComparer.OrdinalIgnoreCase);
        CorrentraPaths paths = dataRoot is null ? CorrentraPaths.Resolve() : CorrentraPaths.Create(dataRoot);
        paths.EnsureCreated();

        var database = new CorrentraDatabase(paths.DatabasePath);
        var repository = new AgentJobRepository(database, new WindowsJobPayloadProtector());
        if (health)
        {
            await repository.InitializeAsync().ConfigureAwait(false);
            Console.WriteLine("{\"healthy\":true,\"component\":\"Correntra.Agent\"}");
            return 0;
        }

        string pipeName = AgentPipeNames.ForCurrentUser();
        using var instanceMutex = new Mutex(true, "Local\\" + pipeName + ".singleton", out bool ownsInstance);
        if (!ownsInstance)
        {
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await using var coordinator = new DownloadJobCoordinator(
                repository,
                new HttpTransferEngine(),
                mediaExecutor: new MediaExecutor());
            await coordinator.InitializeAsync(shutdown.Token).ConfigureAwait(false);
            var dispatcher = new AgentCommandDispatcher(coordinator, new DesktopLauncher());
            var localBridge = new AgentLocalHttpServer(dispatcher);
            _ = Task.Run(() => localBridge.RunAsync(shutdown.Token), CancellationToken.None);
            var server = new AgentPipeServer(pipeName, dispatcher);
            if (runOnce)
            {
                await server.RunOnceAsync(shutdown.Token).ConfigureAwait(false);
            }
            else
            {
                await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            instanceMutex.ReleaseMutex();
        }
    }

    private static string? ReadOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"A value is required after {option}.", nameof(args));
            }

            return args[index + 1];
        }

        return null;
    }
}
