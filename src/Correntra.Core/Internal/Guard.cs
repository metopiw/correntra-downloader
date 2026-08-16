namespace Correntra.Core.Internal;

internal static class Guard
{
    public static string NotNullOrWhiteSpace(string? value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        string trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value cannot exceed {maximumLength} characters.");
        }

        return trimmed;
    }

    public static DateTimeOffset UtcTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }

        return value;
    }

    public static Uri HttpUri(Uri? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (!value.IsAbsoluteUri ||
            (!string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("An absolute HTTP or HTTPS URI is required.", parameterName);
        }

        if (!string.IsNullOrEmpty(value.UserInfo))
        {
            throw new ArgumentException("Credentials must not be embedded in a URI.", parameterName);
        }

        return value;
    }
}
