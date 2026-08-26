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
            // Shared secret for the loopback bridge. Written into the deployed
            // browser-extension folder (readable only from the extension's own
            // pinned origin), so other local processes cannot forge requests.
            string? bridgeToken = null;
            try
            {
                string? extensionFolder = LocateExtensionFolderForToken();
                if (extensionFolder is not null)
                {
                    bridgeToken = BridgeTokenFile.Generate();
                    if (!BridgeTokenFile.TryWrite(extensionFolder, bridgeToken))
                    {
                        bridgeToken = null;
                    }
                }
            }
            catch (Exception)
            {
                bridgeToken = null; // Fall back to Origin-only protection.
            }

            var localBridge = new AgentLocalHttpServer(dispatcher, bridgeToken);
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

    /// <summary>
    /// Finds the browser-extension folder to provision the bridge token into:
    /// next to the agent executable, one level up (Velopack layout), or the
    /// repository root for dev runs. Mirrors ExtensionSetupService's search.
    /// </summary>
    private static string? LocateExtensionFolderForToken()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        string shipped = Path.Combine(baseDirectory, "browser-extension");
        if (File.Exists(Path.Combine(shipped, "manifest.json")))
        {
            return shipped;
        }

        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDirectory));
        if (parent is not null)
        {
            string shippedParent = Path.Combine(parent, "browser-extension");
            if (File.Exists(Path.Combine(shippedParent, "manifest.json")))
            {
                return shippedParent;
            }
        }

        DirectoryInfo? candidate = new(baseDirectory);
        for (int depth = 0; depth < 6 && candidate is not null; depth++)
        {
            string repoCopy = Path.Combine(candidate.FullName, "browser-extension");
            if (File.Exists(Path.Combine(repoCopy, "manifest.json")))
            {
                return repoCopy;
            }

            candidate = candidate.Parent;
        }

        return null;
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
