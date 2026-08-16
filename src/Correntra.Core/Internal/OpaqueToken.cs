namespace Correntra.Core.Internal;

internal static class OpaqueToken
{
    public static string Validate(string? value, string parameterName, int minimumLength = 8, int maximumLength = 128)
    {
        string token = Guard.NotNullOrWhiteSpace(value, parameterName, maximumLength);
        if (token.Length < minimumLength ||
            token.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '~'))
        {
            throw new ArgumentException("An opaque token must use the allowed ASCII token characters.", parameterName);
        }

        return token;
    }
}
