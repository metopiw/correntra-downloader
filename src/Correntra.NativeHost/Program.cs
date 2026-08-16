using Correntra.NativeHost.Protocol;
using Correntra.NativeHost.Runtime;
using Correntra.Platform.Windows.Browser;

return await NativeHostProgram.RunAsync(args).ConfigureAwait(false);

internal static class NativeHostProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains("--health", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("{\"healthy\":true,\"component\":\"Correntra.NativeHost\"}");
            return 0;
        }

        if (args.Contains("--register", StringComparer.OrdinalIgnoreCase))
        {
            bool allowAll = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> ids = ResolveExtensionIds(args, allowAll);
            NativeMessagingRegistration registration = NativeMessagingRegistrar.Register(
                ResolveExecutablePath(),
                ResolveManifestDirectory(),
                ids,
                allowAllChromeExtensionOrigins: allowAll);
            Console.WriteLine(registration.ManifestPath);
            return 0;
        }

        if (args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
        {
            string manifestPath = ResolveManifestPath();
            NativeMessagingRegistrar.Unregister(manifestPath);
            File.Delete(manifestPath);
            return 0;
        }

        bool once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        string? callerOrigin = GetCallerOrigin(args);
        try
        {
            NativeRequestValidator.ValidateCallerOrigin(callerOrigin);
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }

        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        var service = new NativeHostService(new AgentNativeClient());
        try
        {
            await service.RunAsync(input, output, callerOrigin, once).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private static IEnumerable<string> ResolveExtensionIds(string[] args, bool allowAll)
    {
        var ids = new List<string> { NativeRequestValidator.AllowedExtensionId };
        foreach (string arg in args)
        {
            if (arg.StartsWith("--extension-id=", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(arg["--extension-id=".Length..]);
            }
            else if (arg.Equals("--extension-id", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--extension-id requires a value (use --extension-id=<id>).");
            }
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetCallerOrigin(string[] args)
    {
        string[] positional = args
            .Where(static argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        if (positional.Length > 1)
        {
            throw new ArgumentException("Only one browser caller origin is accepted.", nameof(args));
        }

        return positional.FirstOrDefault();
    }

    private static string ResolveExecutablePath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Correntra.NativeHost.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Correntra.NativeHost.exe was not found beside the application files.", path);
    }

    private static string ResolveManifestDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Correntra",
        "Browser");

    private static string ResolveManifestPath() => Path.Combine(
        ResolveManifestDirectory(),
        NativeMessagingRegistrar.HostName + ".json");
}
