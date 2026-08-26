using System.IO.Pipes;
using System.Text.Json;

namespace Correntra.Infrastructure.Ipc;

/// <summary>
/// Describes a request from a secondary Correntra process (the background Agent
/// or a second desktop instance) asking the already-running desktop shell to
/// show the download-confirmation modal for a specific job.
/// </summary>
public sealed record DesktopActivationRequest(
    int ProtocolVersion,
    string Kind,
    string RequestId,
    DateTimeOffset TimestampUtc,
    DesktopActivationPayload Payload);

public sealed record DesktopActivationPayload(string? JobId);

public sealed record DesktopActivationResponse(
    int ProtocolVersion,
    string Kind,
    string RequestId,
    DateTimeOffset TimestampUtc,
    DesktopActivationResult Payload);

public sealed record DesktopActivationResult(bool Accepted, string? Reason);

/// <summary>
/// Well-known message kinds carried over the desktop activation pipe.
/// </summary>
public static class DesktopActivationKinds
{
    public const string ConfirmDownload = "confirm.download";
    public const string Response = "response";
}

/// <summary>
/// Small, testable seam so callers can inject a fake activation transport. The
/// production implementation is <see cref="DesktopActivationClient"/>.
/// </summary>
public interface IDesktopActivationClient
{
    Task<bool> TryConfirmDownloadAsync(string jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Connects to the running desktop shell's activation pipe and asks it to show
/// the confirmation modal for a job. Returns <c>false</c> quickly when no
/// desktop instance is listening, so the caller can fall back to launching one.
/// </summary>
public sealed class DesktopActivationClient : IDesktopActivationClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly LengthPrefixedJsonProtocol _protocol = new(SerializerOptions);
    private readonly string _pipeName;
    private readonly int _connectTimeoutMilliseconds;

    public DesktopActivationClient(string? pipeName = null, int connectTimeoutMilliseconds = 1000)
    {
        _pipeName = pipeName ?? CurrentUserPipeNames.For("Desktop");
        _connectTimeoutMilliseconds = connectTimeoutMilliseconds;
    }

    public async Task<bool> TryConfirmDownloadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        using var timeout = new CancellationTokenSource(_connectTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);

            string requestId = "r_" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            await _protocol.WriteAsync(
                pipe,
                new DesktopActivationRequest(
                    1,
                    DesktopActivationKinds.ConfirmDownload,
                    requestId,
                    DateTimeOffset.UtcNow,
                    new DesktopActivationPayload(jobId)),
                linked.Token).ConfigureAwait(false);

            DesktopActivationResponse? response = await _protocol.ReadAsync<DesktopActivationResponse>(pipe, linked.Token)
                .ConfigureAwait(false);
            return response is { } r &&
                r.ProtocolVersion == 1 &&
                string.Equals(r.Kind, DesktopActivationKinds.Response, StringComparison.Ordinal) &&
                string.Equals(r.RequestId, requestId, StringComparison.Ordinal) &&
                r.Payload.Accepted;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // No desktop is listening (or it closed mid-request); the caller
            // falls back to launching a fresh desktop process.
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public static class DesktopPipeNames
{
    public static string ForCurrentUser() => CurrentUserPipeNames.For("Desktop");
}
