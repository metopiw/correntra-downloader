using Correntra.Core.Downloads;
using Correntra.Core.Internal;
using Correntra.Core.Security;

namespace Correntra.Core.Browser;

public enum BrowserFamily
{
    Chrome = 0,
    Edge = 1,
}

public enum BrowserCaptureDisposition
{
    Accepted = 0,
    ContinueInBrowser = 1,
    Rejected = 2,
}

public readonly record struct BrowserCaptureId
{
    public BrowserCaptureId(string value)
    {
        Value = OpaqueToken.Validate(value, nameof(value));
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? string.Empty;
}

public sealed class BrowserDownloadCapture
{
    public BrowserDownloadCapture(
        BrowserCaptureId id,
        BrowserFamily browser,
        Uri url,
        DateTimeOffset observedAtUtc,
        string? suggestedFileName = null,
        string? contentType = null,
        long? contentLength = null,
        Uri? pageUrl = null,
        Uri? referrer = null,
        DownloadRequestMethod method = DownloadRequestMethod.Get,
        HttpHeaderSet? requestHeaders = null,
        bool userInitiated = true)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A browser capture ID is required.", nameof(id));
        }

        if (!Enum.IsDefined(browser))
        {
            throw new ArgumentOutOfRangeException(nameof(browser));
        }

        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        if (contentLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }

        Id = id;
        Browser = browser;
        Url = Guard.HttpUri(url, nameof(url));
        ObservedAtUtc = Guard.UtcTimestamp(observedAtUtc, nameof(observedAtUtc));
        SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? null
            : SafePath.SanitizeFileName(suggestedFileName);
        ContentType = ValidateContentType(contentType);
        ContentLength = contentLength;
        PageUrl = pageUrl is null ? null : Guard.HttpUri(pageUrl, nameof(pageUrl));
        Referrer = referrer is null ? null : Guard.HttpUri(referrer, nameof(referrer));
        Method = method;
        RequestHeaders = requestHeaders ?? HttpHeaderSet.Empty;
        UserInitiated = userInitiated;
    }

    public BrowserCaptureId Id { get; }

    public BrowserFamily Browser { get; }

    public Uri Url { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string? SuggestedFileName { get; }

    public string? ContentType { get; }

    public long? ContentLength { get; }

    public Uri? PageUrl { get; }

    public Uri? Referrer { get; }

    public DownloadRequestMethod Method { get; }

    public HttpHeaderSet RequestHeaders { get; }

    public bool UserInitiated { get; }

    public bool CanReplaySafely => Method == DownloadRequestMethod.Get;

    public DownloadSource ToDownloadSource(DateTimeOffset? credentialExpiresAtUtc = null)
    {
        if (!CanReplaySafely)
        {
            throw new InvalidOperationException("This browser request cannot be replayed safely.");
        }

        return new DownloadSource(Url, Method, RequestHeaders, Referrer, credentialExpiresAtUtc);
    }

    private static string? ValidateContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string contentType = Guard.NotNullOrWhiteSpace(value, nameof(value), 200);
        if (!contentType.Contains('/') || contentType.Any(char.IsControl))
        {
            throw new ArgumentException("The content type is invalid.", nameof(value));
        }

        return contentType;
    }
}

public sealed record BrowserCaptureResult
{
    public BrowserCaptureResult(
        BrowserCaptureId captureId,
        BrowserCaptureDisposition disposition,
        JobId? jobId = null,
        string? reasonCode = null)
    {
        if (captureId.IsEmpty)
        {
            throw new ArgumentException("A browser capture ID is required.", nameof(captureId));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (jobId is { IsEmpty: true })
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(jobId));
        }

        if (disposition == BrowserCaptureDisposition.Accepted && jobId is null)
        {
            throw new ArgumentException("An accepted capture requires a job ID.", nameof(jobId));
        }

        CaptureId = captureId;
        Disposition = disposition;
        JobId = jobId;
        ReasonCode = reasonCode is null ? null : Guard.NotNullOrWhiteSpace(reasonCode, nameof(reasonCode), 80);
    }

    public BrowserCaptureId CaptureId { get; }

    public BrowserCaptureDisposition Disposition { get; }

    public JobId? JobId { get; }

    public string? ReasonCode { get; }
}
