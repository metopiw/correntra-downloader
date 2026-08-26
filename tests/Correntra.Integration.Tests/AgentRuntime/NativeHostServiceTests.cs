using System.Text.Json;
using Correntra.NativeHost.Protocol;
using Correntra.NativeHost.Runtime;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class NativeHostServiceTests
{
    [Fact]
    public async Task ForwardsValidatedRequestAndReturnsExactExtensionResponse()
    {
        var fakeAgent = new RecordingAgentClient(accepted: true);
        var service = new NativeHostService(fakeAgent);
        await using MemoryStream input = await FrameAsync(new
        {
            protocolVersion = 1,
            kind = "host.ping",
            requestId = "r_ping",
            timestampUtc = "2026-08-13T17:01:02.345Z",
            payload = new { },
        });
        await using var output = new MemoryStream();

        await service.RunAsync(input, output, "chrome-extension://bhnibkknmmodoehpaeoijnkabfdmbdjp/", once: true);

        Assert.Equal("host.ping", fakeAgent.LastRequest?.Kind);
        output.Position = 0;
        using JsonDocument? response = await NativeMessageFraming.ReadDocumentAsync(output);
        Assert.NotNull(response);
        JsonElement root = response.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal("r_ping", root.GetProperty("requestId").GetString());
        JsonElement payload = root.GetProperty("payload");
        Assert.True(payload.GetProperty("accepted").GetBoolean());
        Assert.False(payload.TryGetProperty("reason", out _));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("hostVersion").GetString()));
    }

    [Fact]
    public async Task RejectsInvalidRequestWithoutContactingAgent()
    {
        var fakeAgent = new RecordingAgentClient(accepted: true);
        var service = new NativeHostService(fakeAgent);
        await using MemoryStream input = await FrameAsync(new
        {
            protocolVersion = 1,
            kind = "takeover.offer",
            requestId = "r_bad",
            timestampUtc = "2026-08-13T17:01:02.345Z",
            payload = new { url = "ftp://example.test/file.zip" },
        });
        await using var output = new MemoryStream();

        await service.RunAsync(input, output, callerOrigin: null, once: true);

        Assert.Null(fakeAgent.LastRequest);
        output.Position = 0;
        using JsonDocument? response = await NativeMessageFraming.ReadDocumentAsync(output);
        Assert.NotNull(response);
        Assert.False(response.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean());
        Assert.Equal("invalid-request", response.RootElement.GetProperty("payload").GetProperty("reason").GetString());
    }

    private static async Task<MemoryStream> FrameAsync(object value)
    {
        var stream = new MemoryStream();
        await new NativeMessageFraming().WriteAsync(stream, value);
        stream.Position = 0;
        return stream;
    }

    private sealed class RecordingAgentClient : IAgentNativeClient
    {
        private readonly bool _accepted;

        public RecordingAgentClient(bool accepted) => _accepted = accepted;

        public NativeRequestEnvelope? LastRequest { get; private set; }

        public Task<NativeResponseEnvelope?> SendAsync(
            NativeRequestEnvelope request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<NativeResponseEnvelope?>(
                NativeResponseEnvelope.Create(request.RequestId, _accepted, "agent-test"));
        }
    }
}
