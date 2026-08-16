using Correntra.Core.Internal;
using Correntra.Core.Security;

namespace Correntra.Core.Downloads;

public enum DownloadRequestMethod
{
    Get = 0,
    Post = 1,
}

public sealed class DownloadSource
{
    public DownloadSource(
        Uri url,
        DownloadRequestMethod method = DownloadRequestMethod.Get,
        HttpHeaderSet? headers = null,
        Uri? referrer = null,
        DateTimeOffset? credentialExpiresAtUtc = null)
    {
        Url = Guard.HttpUri(url, nameof(url));
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        Method = method;
        Headers = headers ?? HttpHeaderSet.Empty;
        Referrer = referrer is null ? null : Guard.HttpUri(referrer, nameof(referrer));

        if (credentialExpiresAtUtc is { } expiry)
        {
            CredentialExpiresAtUtc = Guard.UtcTimestamp(expiry, nameof(credentialExpiresAtUtc));
        }
    }

    public Uri Url { get; }

    public DownloadRequestMethod Method { get; }

    public HttpHeaderSet Headers { get; }

    public Uri? Referrer { get; }

    public DateTimeOffset? CredentialExpiresAtUtc { get; }

    public bool ContainsCredentials => Headers.ContainsSensitiveValues;

    public bool CredentialsAreExpired(DateTimeOffset nowUtc)
    {
        Guard.UtcTimestamp(nowUtc, nameof(nowUtc));
        return CredentialExpiresAtUtc is { } expiry && nowUtc >= expiry;
    }
}
