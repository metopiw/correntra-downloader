using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Correntra.Core;

namespace Correntra.Infrastructure.Ipc;

/// <summary>
/// Named pipe endpoint hosted by the running desktop shell. A background Agent
/// (or a second desktop instance) connects to ask the shell to surface the
/// download-confirmation modal. Requests are validated and handed to an
/// injectable handler that runs on the caller's behalf.
/// </summary>
public sealed class DesktopActivationServer
{
    private const int MaximumServerInstances = 4;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _pipeName;
    private readonly Func<string, CancellationToken, Task<bool>> _handler;
    private readonly LengthPrefixedJsonProtocol _protocol = new(SerializerOptions);

    public DesktopActivationServer(string? pipeName, Func<string, CancellationToken, Task<bool>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _pipeName = pipeName ?? DesktopPipeNames.ForCurrentUser();
        _handler = handler;
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
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    continue;
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
            await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            DesktopActivationRequest? request = await _protocol.ReadAsync<DesktopActivationRequest>(server, cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            bool accepted = false;
            string? reason = null;
            if (IsValid(request, out string? invalidReason))
            {
                accepted = await _handler(request.Payload.JobId!, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                reason = invalidReason;
            }

            await _protocol.WriteAsync(
                server,
                new DesktopActivationResponse(
                    1,
                    DesktopActivationKinds.Response,
                    request.RequestId,
                    DateTimeOffset.UtcNow,
                    new DesktopActivationResult(accepted, reason)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            // An untrusted or disconnected client must not terminate the listener.
        }
    }

    private static bool IsValid(DesktopActivationRequest request, out string? reason)
    {
        reason = null;
        if (request.ProtocolVersion != 1)
        {
            reason = "unsupported-protocol";
            return false;
        }

        if (!string.Equals(request.Kind, DesktopActivationKinds.ConfirmDownload, StringComparison.Ordinal))
        {
            reason = "unsupported-command";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
        {
            reason = "invalid-request";
            return false;
        }

        if (request.TimestampUtc.Offset != TimeSpan.Zero)
        {
            reason = "invalid-request";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Payload.JobId) ||
            !JobId.TryParse(request.Payload.JobId, out _))
        {
            reason = "invalid-request";
            return false;
        }

        return true;
    }
}