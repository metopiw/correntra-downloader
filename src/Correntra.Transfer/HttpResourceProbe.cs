using System.Net;
using System.Net.Http.Headers;

namespace Correntra.Transfer;

public sealed class HttpResourceProbe
{
    private readonly HttpClient client;

    public HttpResourceProbe(HttpClient client) =>
        this.client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<RemoteResourceInfo> ProbeAsync(
        Uri source,
        IReadOnlyDictionary<string, string>? headers = null,
        RetryOptions? retry = null,
        PauseToken pauseToken = default,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        headers ??= new Dictionary<string, string>();
        retry ??= new RetryOptions();

        ResponseSnapshot? head = null;
        try
        {
            using var headResponse = await HttpTransferUtilities.SendWithRetryAsync(
                client,
                () => HttpTransferUtilities.CreateRequest(HttpMethod.Head, source, headers),
                HttpCompletionOption.ResponseHeadersRead,
                retry,
                pauseToken,
                cancellationToken).ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                head = ResponseSnapshot.From(headResponse);
            }
        }
        catch (Exception exception) when (HttpTransferUtilities.IsTransient(exception, cancellationToken))
        {
            // A range GET below remains a standards-compliant probe fallback.
        }

        ResponseSnapshot? ranged = null;
        HttpResponseMessage? rangeResponse = null;
        try
        {
            rangeResponse = await HttpTransferUtilities.SendWithRetryAsync(
                client,
                () => CreateRangeProbeRequest(source, headers),
                HttpCompletionOption.ResponseHeadersRead,
                retry,
                pauseToken,
                cancellationToken).ConfigureAwait(false);

            if (rangeResponse.IsSuccessStatusCode || rangeResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                ranged = ResponseSnapshot.From(rangeResponse);
            }
            else if (head is null)
            {
                rangeResponse.EnsureSuccessStatusCode();
            }
        }
        catch (Exception exception) when (head is not null && HttpTransferUtilities.IsTransient(exception, cancellationToken))
        {
            // HEAD metadata is sufficient when a cautious capability probe fails transiently.
        }
        finally
        {
            rangeResponse?.Dispose();
        }

        var metadata = ranged?.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && head is not null
            ? head
            : ranged ?? head ?? throw new TransferException("The remote resource could not be probed.");
        var supportsRanges = ranged?.HasValidProbeRange == true;
        var length = ranged?.TotalLength ??
                     (ranged?.StatusCode == HttpStatusCode.OK ? ranged.ContentLength : null) ??
                     head?.ContentLength;
        if (metadata.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && length is null)
        {
            length = 0;
        }

        return new RemoteResourceInfo(
            source,
            metadata.FinalUri,
            metadata.StatusCode,
            length,
            supportsRanges,
            metadata.EntityTag ?? head?.EntityTag,
            metadata.LastModified ?? head?.LastModified,
            GetSuggestedFileName(metadata.ContentDisposition ?? head?.ContentDisposition, metadata.FinalUri),
            metadata.ContentType ?? head?.ContentType);
    }

    private static HttpRequestMessage CreateRangeProbeRequest(
        Uri source,
        IReadOnlyDictionary<string, string> headers)
    {
        var request = HttpTransferUtilities.CreateRequest(HttpMethod.Get, source, headers);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        return request;
    }

    private static string GetSuggestedFileName(ContentDispositionHeaderValue? disposition, Uri finalUri)
    {
        var candidate = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            candidate = candidate.Trim().Trim('"');
            try
            {
                candidate = Uri.UnescapeDataString(candidate);
            }
            catch (UriFormatException)
            {
                // Keep the undecoded server value.
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Uri.UnescapeDataString(Path.GetFileName(finalUri.AbsolutePath));
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = "download";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(candidate.Select(character =>
            invalid.Contains(character) || character is '/' or '\\' ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized;
    }

    private static void ValidateSource(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri ||
            (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS addresses are supported.", nameof(source));
        }
    }

    private sealed record ResponseSnapshot(
        HttpStatusCode StatusCode,
        Uri FinalUri,
        long? ContentLength,
        long? TotalLength,
        bool HasValidProbeRange,
        string? EntityTag,
        DateTimeOffset? LastModified,
        ContentDispositionHeaderValue? ContentDisposition,
        string? ContentType)
    {
        public static ResponseSnapshot From(HttpResponseMessage response)
        {
            var contentRange = response.Content.Headers.ContentRange;
            var validProbe = response.StatusCode == HttpStatusCode.PartialContent &&
                             contentRange?.From == 0 &&
                             contentRange.To == 0;

            return new ResponseSnapshot(
                response.StatusCode,
                response.RequestMessage?.RequestUri ?? throw new TransferException("The HTTP response has no final URI."),
                response.Content.Headers.ContentLength,
                contentRange?.Length,
                validProbe,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                response.Content.Headers.ContentDisposition,
                response.Content.Headers.ContentType?.MediaType);
        }
    }
}
