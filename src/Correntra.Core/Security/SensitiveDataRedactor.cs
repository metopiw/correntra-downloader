using System.Text;
using System.Text.RegularExpressions;

namespace Correntra.Core.Security;

public static partial class SensitiveDataRedactor
{
    public const string RedactedValue = "[REDACTED]";

    public static string RedactUri(Uri? uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute URI is required.", nameof(uri));
        }

        UriBuilder builder = new(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty,
            Query = string.IsNullOrEmpty(uri.Query) ? string.Empty : RedactedValue,
        };

        return builder.Uri.AbsoluteUri;
    }

    public static string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string redacted = AuthorizationPattern().Replace(value, "$1" + RedactedValue);
        redacted = CookiePattern().Replace(redacted, "$1" + RedactedValue);
        return QuerySecretPattern().Replace(redacted, "$1" + RedactedValue);
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*)(?:bearer\\s+|basic\\s+)?[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("(?i)((?:set-)?cookie\\s*[:=]\\s*)[^\\r\\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex CookiePattern();

    [GeneratedRegex("(?i)([?&](?:token|access_token|auth|authorization|signature|sig|key|api_key|credential|password)=)[^&#\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretPattern();
}
