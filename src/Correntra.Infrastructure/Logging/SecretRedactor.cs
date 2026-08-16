using System.Text.RegularExpressions;

namespace Correntra.Infrastructure.Logging;

public static partial class SecretRedactor
{
    [GeneratedRegex("(?i)(cookie|authorization|proxy-authorization|x-api-key)\\s*[:=]\\s*([^\\r\\n;,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderSecretRegex();

    [GeneratedRegex("(?i)([?&](?:token|sig|signature|auth|key|x-amz-signature|x-goog-signature)=)[^&#\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string result = HeaderSecretRegex().Replace(value, "$1=[REDACTED]");
        return QuerySecretRegex().Replace(result, "$1[REDACTED]");
    }
}

