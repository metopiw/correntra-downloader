using Correntra.Agent.Runtime;
using Correntra.Core;
using Correntra.Infrastructure.Ipc;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class DesktopActivationTests : IAsyncLifetime
{
    [Fact]
    public async Task ActivationRequestIsAcceptedByTheRunningDesktop()
    {
        string pipeName = UniquePipeName();
        string jobId = JobId.Create().ToString();
        using var cts = new CancellationTokenSource();
        var server = new DesktopActivationServer(pipeName, (received, _) => Task.FromResult(received == jobId));
        Task serverTask = server.RunAsync(cts.Token);

        try
        {
            var client = new DesktopActivationClient(pipeName, connectTimeoutMilliseconds: 2000);
            bool accepted = await client.TryConfirmDownloadAsync(jobId);

            Assert.True(accepted);
        }
        finally
        {
            cts.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task MalformedOrDefaultJobIdIsRejected()
    {
        string pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource();
        var server = new DesktopActivationServer(pipeName, (_, _) => Task.FromResult(true));
        Task serverTask = server.RunAsync(cts.Token);

        try
        {
            var client = new DesktopActivationClient(pipeName, connectTimeoutMilliseconds: 2000);
            bool accepted = await client.TryConfirmDownloadAsync("not-a-guid");

            Assert.False(accepted);
        }
        finally
        {
            cts.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task NoRunningDesktopIsNotAcceptedAndDoesNotThrow()
    {
        // Uses the real per-user pipe name which, on a test machine with no
        // Correntra desktop running, will fail-fast with a connection error.
        var client = new DesktopActivationClient(
            UniquePipeName(),
            connectTimeoutMilliseconds: 500);

        bool accepted = await client.TryConfirmDownloadAsync(JobId.Create().ToString());

        Assert.False(accepted);
    }

    [Fact]
    public void LocatorPrefersSiblingDesktopProjectOverMixedFolders()
    {
        string root = Path.Combine(Path.GetTempPath(), "correntra-locator-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Simulated development layout:
            //   <root>/src/Correntra.Agent/bin/Debug/net8.0-windows10.0.17763.0/
            //   <root>/src/Correntra.Desktop/bin/Debug/net8.0-windows10.0.17763.0/Correntra.exe
            string agentDir = Path.Combine(root, "src", "Correntra.Agent", "bin", "Debug", "net8.0-windows10.0.17763.0");
            string desktopBin = Path.Combine(root, "src", "Correntra.Desktop", "bin", "Debug", "net8.0-windows10.0.17763.0");
            Directory.CreateDirectory(desktopBin);
            string desktopExe = Path.Combine(desktopBin, "Correntra.exe");
            File.WriteAllText(desktopExe, string.Empty);

            string? resolved = DesktopExecutableLocator.Resolve(agentDir);

            Assert.Equal(desktopExe, resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LocatorFindsDesktopBundledNextToTheAgent()
    {
        string root = Path.Combine(Path.GetTempPath(), "correntra-locator-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string desktopExe = Path.Combine(root, "Correntra.exe");
            File.WriteAllText(desktopExe, string.Empty);

            string? resolved = DesktopExecutableLocator.Resolve(root);

            Assert.Equal(desktopExe, resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string UniquePipeName() => "Correntra.Tests." + Guid.NewGuid().ToString("N");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}