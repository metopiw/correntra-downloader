using System.Collections.Immutable;
using System.Text;

namespace Correntra.Core.Security;

public sealed class HttpHeaderSet
{
    public const int MaximumHeaderCount = 100;
    public const int MaximumNameLength = 128;
    public const int MaximumValueLength = 16 * 1024;
    public const int MaximumAggregateLength = 64 * 1024;
    public const string RedactedValue = "[REDACTED]";

    private static readonly ImmutableHashSet<string> SensitiveNames =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "X-Api-Key",
            "Api-Key",
            "X-Auth-Token",
            "X-Csrf-Token");

    private readonly ImmutableDictionary<string, string> _values;

    public HttpHeaderSet(IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        ImmutableDictionary<string, string>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        int aggregateLength = 0;
        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                ValidateName(header.Key);
                ValidateValue(header.Value, header.Key);

                if (builder.ContainsKey(header.Key))
                {
                    throw new ArgumentException($"Duplicate HTTP header '{header.Key}'.", nameof(headers));
                }

                aggregateLength = checked(aggregateLength + header.Key.Length + header.Value.Length);
                if (aggregateLength > MaximumAggregateLength)
                {
                    throw new ArgumentException("HTTP headers exceed the aggregate size limit.", nameof(headers));
                }

                builder.Add(header.Key, header.Value);
                if (builder.Count > MaximumHeaderCount)
                {
                    throw new ArgumentException("Too many HTTP headers were supplied.", nameof(headers));
                }
            }
        }

        _values = builder.ToImmutable();
    }

    public static HttpHeaderSet Empty { get; } = new();

    public int Count => _values.Count;

    public IEnumerable<string> Names => _values.Keys;

    public bool ContainsSensitiveValues => _values.Keys.Any(IsSensitiveName);

    public string this[string name] => _values[name];

    public bool TryGetValue(string name, out string? value) => _values.TryGetValue(name, out value);

    public IReadOnlyDictionary<string, string> AsReadOnly() => _values;

    public HttpHeaderSet Redacted()
    {
        return new HttpHeaderSet(_values.Select(static pair =>
            new KeyValuePair<string, string>(
                pair.Key,
                IsSensitiveName(pair.Key) ? RedactedValue : pair.Value)));
    }

    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (SensitiveNames.Contains(name))
        {
            return true;
        }

        return name.EndsWith("-Token", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-Secret", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-Credential", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength)
        {
            throw new ArgumentException("An HTTP header name is empty or too long.", nameof(name));
        }

        foreach (char character in name)
        {
            if (!IsTokenCharacter(character))
            {
                throw new ArgumentException("An HTTP header name contains an invalid character.", nameof(name));
            }
        }
    }

    public static void ValidateValue(string? value, string? headerName = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumValueLength)
        {
            throw new ArgumentException("An HTTP header value is too long.", headerName ?? nameof(value));
        }

        foreach (char character in value)
        {
            if (character is '\r' or '\n' or '\0' || (char.IsControl(character) && character != '\t'))
            {
                throw new ArgumentException(
                    "An HTTP header value contains a forbidden control character.",
                    headerName ?? nameof(value));
            }
        }
    }

    private static bool IsTokenCharacter(char character)
    {
        if (character > 127 || char.IsLetterOrDigit(character))
        {
            return character <= 127;
        }

        return character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
