using System.Reflection;
using System.Text.Json;
using Correntra.NativeHost.Protocol;

namespace Correntra.NativeHost.Runtime;

public sealed class NativeHostService
{
    private static readonly string HostVersion =
        typeof(NativeHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(NativeHostService).Assembly.GetName().Version?.ToString()
        ?? "0.1.0";
    private readonly NativeMessageFraming _framing;
    private readonly IAgentNativeClient _agentClient;

    public NativeHostService(
        IAgentNativeClient agentClient,
        NativeMessageFraming? framing = null)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _framing = framing ?? new NativeMessageFraming();
    }

    public async Task RunAsync(
        Stream input,
        Stream output,
        string? callerOrigin,
        bool once,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        NativeRequestValidator.ValidateCallerOrigin(callerOrigin);

        do
        {
            JsonDocument? document;
            try
            {
                document = await NativeMessageFraming.ReadDocumentAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or JsonException)
            {
                return;
            }

            if (document is null)
            {
                return;
            }

            using (document)
            {
                NativeRequestEnvelope request;
                try
                {
                    request = NativeRequestValidator.Validate(document.RootElement);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
                {
                    string requestId = TryReadSafeRequestId(document.RootElement) ?? "invalid";
                    await _framing.WriteAsync(
                        output,
                        NativeResponseEnvelope.Create(requestId, false, HostVersion, "invalid-request"),
                        cancellationToken).ConfigureAwait(false);
                    if (once)
                    {
                        return;
                    }

                    continue;
                }

                NativeResponseEnvelope response;
                try
                {
                    NativeResponseEnvelope? agentResponse = await _agentClient.SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    response = agentResponse is null
                        ? NativeResponseEnvelope.Create(request.RequestId, false, HostVersion, "agent-unavailable")
                        : NativeResponseEnvelope.Create(
                            request.RequestId,
                            agentResponse.Payload.Accepted,
                            HostVersion,
                            agentResponse.Payload.Reason,
                            agentResponse.Payload.JobId,
                            agentResponse.Payload.MediaQualities);
                }
                catch (Exception exception) when (
                    exception is IOException or OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
                {
                    response = NativeResponseEnvelope.Create(request.RequestId, false, HostVersion, "agent-unavailable");
                }

                await _framing.WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            }
        }
        while (!once && !cancellationToken.IsCancellationRequested);
    }

    private static string? TryReadSafeRequestId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("requestId", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? requestId = value.GetString();
        return requestId is { Length: > 0 and <= 128 } &&
            requestId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? requestId
            : null;
    }
}
