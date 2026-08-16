using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Correntra.Infrastructure.Ipc;

namespace Correntra.Agent.Runtime;

public sealed class AgentPipeServer
{
    private const int MaximumServerInstances = 16;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
    private readonly string _pipeName;
    private readonly AgentCommandDispatcher _dispatcher;
    private readonly LengthPrefixedJsonProtocol _protocol = new(SerializerOptions);

    public AgentPipeServer(string pipeName, AgentCommandDispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clients = new ConcurrentDictionary<long, Task>();
        long clientId = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = CreateServer();
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                long id = Interlocked.Increment(ref clientId);
                Task client = HandleClientAndDisposeAsync(server, cancellationToken);
                clients.TryAdd(id, client);
                _ = client.ContinueWith(
                    completedTask => clients.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(clients.Values).ConfigureAwait(false);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream server = CreateServer();
        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        await HandleClientAsync(server, singleRequest: true, cancellationToken).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreateServer()
    {
        PipeOptions options = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            options |= PipeOptions.CurrentUserOnly;
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            MaximumServerInstances,
            PipeTransmissionMode.Byte,
            options,
            16 * 1024,
            16 * 1024);
    }

    private async Task HandleClientAndDisposeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            await HandleClientAsync(server, singleRequest: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream server,
        bool singleRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            do
            {
                AgentRequestEnvelope? request = await _protocol.ReadAsync<AgentRequestEnvelope>(server, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                AgentResponseEnvelope response = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                await _protocol.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
            }
            while (!singleRequest && server.IsConnected && !cancellationToken.IsCancellationRequested);
        }
        catch (Exception exception) when (
            (exception is IOException or InvalidDataException or JsonException) && !cancellationToken.IsCancellationRequested)
        {
            // An untrusted or disconnected client must not terminate the Agent listener.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
