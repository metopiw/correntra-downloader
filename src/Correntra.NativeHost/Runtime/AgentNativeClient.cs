using System.Diagnostics;
using System.IO.Pipes;
using Correntra.Infrastructure.Ipc;
using Correntra.NativeHost.Protocol;

namespace Correntra.NativeHost.Runtime;

public interface IAgentNativeClient
{
    Task<NativeResponseEnvelope?> SendAsync(
        NativeRequestEnvelope request,
        CancellationToken cancellationToken = default);
}

public sealed class AgentNativeClient : IAgentNativeClient
{
    private static readonly TimeSpan ConnectionBudget = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(120);
    private readonly string _pipeName;
    private readonly string _agentExecutablePath;
    private readonly LengthPrefixedJsonProtocol _protocol = new();

    public AgentNativeClient(string? pipeName = null, string? agentExecutablePath = null)
    {
        _pipeName = pipeName ?? AgentPipeNames.ForCurrentUser();
        _agentExecutablePath = Path.GetFullPath(
            agentExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "Correntra.Agent.exe"));
    }

    public async Task<NativeResponseEnvelope?> SendAsync(
        NativeRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Media commands run real extractors (yt-dlp) inside the agent and can
        // take many seconds; a handshake-sized budget would drop every reply.
        TimeSpan commandBudget = request.Kind switch
        {
            "media.resolve" => TimeSpan.FromSeconds(30),
            "media.start" => TimeSpan.FromSeconds(10),
            _ => ConnectionBudget,
        };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(commandBudget);
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool agentStartAttempted = false;

        while (stopwatch.Elapsed < ConnectionBudget)
        {
            NamedPipeClientStream? pipe = await TryConnectAsync(budget.Token).ConfigureAwait(false);
            if (pipe is not null)
            {
                await using (pipe.ConfigureAwait(false))
                {
                    await _protocol.WriteAsync(pipe, request, budget.Token).ConfigureAwait(false);
                    NativeResponseEnvelope? response = await _protocol.ReadAsync<NativeResponseEnvelope>(pipe, budget.Token)
                        .ConfigureAwait(false);
                    return IsMatchingResponse(response, request.RequestId) ? response : null;
                }
            }

            if (!agentStartAttempted)
            {
                agentStartAttempted = true;
                TryStartAgent();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), budget.Token).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(AttemptTimeout);
            await pipe.ConnectAsync(attempt.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private void TryStartAgent()
    {
        if (!File.Exists(_agentExecutablePath))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _agentExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_agentExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using Process? process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool IsMatchingResponse(NativeResponseEnvelope? response, string requestId) =>
        response is
        {
            ProtocolVersion: 1,
            Kind: "response",
            Payload: not null,
        } && string.Equals(response.RequestId, requestId, StringComparison.Ordinal);
}

