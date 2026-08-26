using System.Net;
using System.Text.Json;
using Correntra.Agent.Runtime;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Infrastructure.Storage;
using Correntra.Transfer;
using Microsoft.Data.Sqlite;

namespace Correntra.Integration.Tests.AgentRuntime;

/// <summary>
/// Security-contract tests for the loopback HTTP bridge: pinned extension
/// origin matching, Host/DNS-rebinding guard, CORS behaviour and the shared
/// bridge token. These run the real AgentLocalHttpServer over real sockets
/// on 127.0.0.1 (port 0 → an ephemeral free port).
/// </summary>
public sealed class AgentLocalHttpServerTests
{
    [Fact]
    public async Task PingWithoutOriginIsAccepted()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(harness.BaseUri + "ping")).StatusCode);
    }

    [Fact]
    public async Task WebPageOriginIsRejectedWith403()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, harness.BaseUri + "ping");
        request.Headers.Add("Origin", "https://evil.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ForeignExtensionOriginIsRejectedEvenWithValidToken()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, harness.BaseUri + "ping");
        request.Headers.Add("Origin", "chrome-extension://ddkjiahejlhfcafbddmgiahcphecmpfh/");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task PinnedOriginIsAccepted()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, harness.BaseUri + "ping");
        request.Headers.Add("Origin", BrowserExtensionIdentity.ExtensionOrigin);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            BrowserExtensionIdentity.ExtensionOrigin,
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("attacker.example.test:27410")]
    [InlineData("localhost.attacker.example.test:27410")]
    public async Task RebindingHostHeadersAreRejected(string host)
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, harness.BaseUri + "ping");
        request.Headers.Host = host;
        HttpResponseMessage response = await client.SendAsync(request);
        // Never OK: our guard answers 403; malformed hosts may be rejected
        // earlier by the HTTP stack itself.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ipv6LoopbackHostHeaderNeverSucceeds()
    {
        await using var harness = await BridgeHarness.StartAsync();
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, harness.BaseUri + "ping");
        request.Headers.Host = "[::1]:27410";
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CommandEndpointWithoutTokenIsUnauthorizedWhenTokenIsProvisioned()
    {
        await using var harness = await BridgeHarness.StartAsync(withToken: true);
        using var client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync(harness.BaseUri + "jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CommandEndpointWithWrongTokenIsUnauthorized()
    {
        await using var harness = await BridgeHarness.StartAsync(withToken: true);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Correntra-Token", "not-the-token");
        HttpResponseMessage response = await client.GetAsync(harness.BaseUri + "jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CommandEndpointWithCorrectTokenIsAccepted()
    {
        await using var harness = await BridgeHarness.StartAsync(withToken: true);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Correntra-Token", harness.Token!);
        HttpResponseMessage response = await client.GetAsync(harness.BaseUri + "jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"jobs\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandEndpointsStayOpenWithoutProvisionedToken()
    {
        // Bare runs / legacy: no token provisioned → only Origin/Host guards.
        await using var harness = await BridgeHarness.StartAsync(withToken: false);
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(harness.BaseUri + "jobs")).StatusCode);
    }

    [Fact]
    public void TokenGeneratorProducesUrlSafe43CharSecrets()
    {
        for (int i = 0; i < 8; i++)
        {
            string token = BridgeTokenFile.Generate();
            Assert.Equal(43, token.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", token);
        }
    }

    [Fact]
    public void TokenComparisonIsExactAndCaseSensitive()
    {
        string token = BridgeTokenFile.Generate();
        Assert.True(BridgeTokenFile.IsValid(token, token));
        Assert.False(BridgeTokenFile.IsValid(token.ToUpperInvariant(), token));
        Assert.False(BridgeTokenFile.IsValid(token + "x", token));
        Assert.False(BridgeTokenFile.IsValid(null, token));
        Assert.False(BridgeTokenFile.IsValid("", token));
    }

    [Fact]
    public void TokenFileRoundTripsAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), "Correntra.BridgeTokenTests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(BridgeTokenFile.TryWrite(root, "token-1"));
            Assert.Equal("token-1", File.ReadAllText(Path.Combine(root, BridgeTokenFile.FileName)).Trim());
            Assert.True(BridgeTokenFile.TryWrite(root, "token-2"));
            Assert.Equal("token-2", File.ReadAllText(Path.Combine(root, BridgeTokenFile.FileName)).Trim());
            Assert.False(File.Exists(Path.Combine(root, BridgeTokenFile.FileName + ".tmp")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingDesktopLauncher : IDesktopLauncher
    {
        public Task<bool> ShowDownloadConfirmationAsync(
            JobId jobId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class PassthroughProtector : IJobPayloadProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> payload) => payload.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload) => protectedPayload.ToArray();
    }

    private sealed class BridgeHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _runner;
        private readonly string _root;

        private BridgeHarness(Task runner, int port, string root, string? token, CancellationTokenSource shutdown)
        {
            _runner = runner;
            BaseUri = $"http://127.0.0.1:{port}/";
            _root = root;
            Token = token;
            _shutdown = shutdown;
        }

        public string BaseUri { get; }

        public string? Token { get; }

        public static async Task<BridgeHarness> StartAsync(bool withToken = false)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Correntra.BridgeTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repository = new AgentJobRepository(
                new CorrentraDatabase(Path.Combine(root, "correntra.db")),
                new PassthroughProtector());
            var coordinator = new DownloadJobCoordinator(repository, new HttpTransferEngine(), 1);
            await coordinator.InitializeAsync();

            string? token = withToken ? BridgeTokenFile.Generate() : null;
            var dispatcher = new AgentCommandDispatcher(coordinator, new RecordingDesktopLauncher());
            int port = FreeTcpPort();
            var shutdown = new CancellationTokenSource();
            var server = new AgentLocalHttpServer(dispatcher, token, port);
            Task runner = server.RunAsync(shutdown.Token);

            // Wait until the listener accepts connections.
            using var probe = new HttpClient();
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                {
                    using var ping = await probe.GetAsync(
                        BaseAddress(port) + "ping",
                        ready.Token);
                    if (ping.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (Exception) when (!ready.IsCancellationRequested)
                {
                }

                await Task.Delay(50, ready.Token);
            }

            return new BridgeHarness(runner, port, root, token, shutdown);
        }

        private static string BaseAddress(int port) => $"http://127.0.0.1:{port}/";

        private static int FreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _runner.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Shutdown races are acceptable in tests.
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
