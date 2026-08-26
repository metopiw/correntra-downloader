using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Correntra.Core;
using Correntra.Core.Security;

namespace Correntra.NativeHost.Protocol;

public sealed partial class NativeRequestValidator
{
    public const string AllowedExtensionId = BrowserExtensionIdentity.ExtensionId;
    private static readonly HashSet<string> EnvelopeProperties =
        new(StringComparer.Ordinal) { "protocolVersion", "kind", "requestId", "timestampUtc", "payload" };
    private static readonly HashSet<string> AllowedKinds =
        new(StringComparer.Ordinal) { "host.ping", "takeover.offer", "media.start", "media.resolve" };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Host",
        "Proxy-Authorization",
        "Transfer-Encoding",
    };

    public static NativeRequestEnvelope Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The native message must be a JSON object.");
        }

        JsonProperty[] properties = root.EnumerateObject().ToArray();
        if (properties.Length != EnvelopeProperties.Count ||
            properties.Any(property => !EnvelopeProperties.Contains(property.Name)) ||
            properties.Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() != EnvelopeProperties.Count)
        {
            throw new InvalidDataException("The native message envelope has unexpected or duplicate fields.");
        }

        if (!root.TryGetProperty("protocolVersion", out JsonElement protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.Number ||
            !protocolVersion.TryGetInt32(out int version) ||
            version != 1)
        {
            throw new InvalidDataException("The native protocol version is unsupported.");
        }

        string kind = ReadRequiredString(root, "kind", 80);
        if (!AllowedKinds.Contains(kind))
        {
            throw new InvalidDataException("The native command is not allowed.");
        }

        string requestId = ReadRequiredString(root, "requestId", 128);
        if (!RequestIdRegex().IsMatch(requestId))
        {
            throw new InvalidDataException("The native request ID is invalid.");
        }

        string timestampText = ReadRequiredString(root, "timestampUtc", 64);
        if (!DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp) ||
            timestamp.Offset != TimeSpan.Zero ||
            !(timestampText.EndsWith('Z') || timestampText.EndsWith("+00:00", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The native timestamp must be an ISO-8601 UTC timestamp.");
        }

        if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The native payload must be a JSON object.");
        }

        RejectLineBreaks(payload);
        switch (kind)
        {
            case "host.ping":
                break;
            case "takeover.offer":
                ValidateTakeover(payload);
                break;
            case "media.start":
                ValidateMediaStart(payload);
                break;
            case "media.resolve":
                ValidateMediaResolve(payload);
                break;
        }

        return new NativeRequestEnvelope(version, kind, requestId, timestamp, payload.Clone());
    }

    public static void ValidateCallerOrigin(string? callerOrigin)
    {
        if (callerOrigin is null)
        {
            return;
        }

        if (IsAllowedExtensionOrigin(callerOrigin))
        {
            return;
        }

        throw new UnauthorizedAccessException("The browser extension origin is not allowed.");
    }

    /// <summary>
    /// Accepts only the canonical extension origin <c>chrome-extension://&lt;id&gt;/</c>.
    /// The packaged manifest pins a fixed <c>key</c>, so Chrome derives the same
    /// 32-character ID on every install (unpacked or not) and the bridge can
    /// match it exactly instead of trusting any well-formed extension origin.
    /// </summary>
    private static bool IsAllowedExtensionOrigin(string origin)
    {
        return string.Equals(
            origin,
            BrowserExtensionIdentity.ExtensionOrigin,
            StringComparison.Ordinal);
    }

    private static void ValidateTakeover(JsonElement payload)
    {
        Uri? url = ReadOptionalHttpUri(payload, "url");
        Uri? finalUrl = ReadOptionalHttpUri(payload, "finalUrl");
        if (url is null && finalUrl is null)
        {
            throw new InvalidDataException("A takeover offer requires an HTTP or HTTPS URL.");
        }

        ValidateOptionalHeaders(payload, "headers");
        _ = ReadOptionalHttpUri(payload, "referrer");
        _ = ReadOptionalString(payload, "filename", 260);
        _ = ReadOptionalString(payload, "mime", 200);
    }

    private static void ValidateMediaStart(JsonElement payload)
    {
        _ = ReadRequiredHttpUri(payload, "url");
        _ = ReadOptionalHttpUri(payload, "referrer");
        string candidateId = ReadRequiredString(payload, "candidateId", 128);
        if (!CandidateIdRegex().IsMatch(candidateId))
        {
            throw new InvalidDataException("The media candidate ID is invalid.");
        }
        if (!payload.TryGetProperty("media", out JsonElement media) || media.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A media start request requires media details.");
        }

        if (payload.TryGetProperty("authContext", out JsonElement authContext))
        {
            if (authContext.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The authentication context is invalid.");
            }

            ValidateOptionalHeaders(authContext, "headers");
        }
    }

    private static void ValidateMediaResolve(JsonElement payload)
    {
        _ = ReadRequiredHttpUri(payload, "url");
        _ = ReadOptionalHttpUri(payload, "referrer");
        _ = ReadOptionalString(payload, "candidateId", 128);
        _ = ReadOptionalString(payload, "title", 500);
        ValidateOptionalHeaders(payload, "headers");
    }

    private static void ValidateOptionalHeaders(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out JsonElement headers))
        {
            return;
        }

        if (headers.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("HTTP headers must be a JSON object.");
        }

        int count = 0;
        int aggregateLength = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty header in headers.EnumerateObject())
        {
            if (++count > HttpHeaderSet.MaximumHeaderCount ||
                header.Value.ValueKind != JsonValueKind.String ||
                !names.Add(header.Name))
            {
                throw new InvalidDataException("The HTTP header collection is invalid.");
            }

            string value = header.Value.GetString() ?? string.Empty;
            HttpHeaderSet.ValidateName(header.Name);
            HttpHeaderSet.ValidateValue(value, header.Name);
            if (ForbiddenHeaders.Contains(header.Name))
            {
                throw new InvalidDataException("The HTTP header is managed by Correntra.");
            }

            aggregateLength = checked(aggregateLength + header.Name.Length + value.Length);
            if (aggregateLength > HttpHeaderSet.MaximumAggregateLength)
            {
                throw new InvalidDataException("The HTTP headers exceed the size limit.");
            }
        }
    }

    private static Uri ReadRequiredHttpUri(JsonElement payload, string name) =>
        ReadOptionalHttpUri(payload, name) ?? throw new InvalidDataException($"'{name}' is required.");

    private static Uri? ReadOptionalHttpUri(JsonElement payload, string name)
    {
        string? text = ReadOptionalString(payload, name, 16_384);
        if (text is null)
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Only credential-free absolute HTTP and HTTPS URLs are accepted.");
        }

        return uri;
    }

    private static string ReadRequiredString(JsonElement payload, string name, int maximumLength) =>
        ReadOptionalString(payload, name, maximumLength) ?? throw new InvalidDataException($"'{name}' is required.");

    private static string? ReadOptionalString(JsonElement payload, string name, int maximumLength)
    {
        if (!payload.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"'{name}' must be a string.");
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || ContainsLineBreak(text))
        {
            throw new InvalidDataException($"'{name}' is invalid.");
        }

        return text;
    }

    private static void RejectLineBreaks(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (ContainsLineBreak(property.Name))
                    {
                        throw new InvalidDataException("JSON property names cannot contain line breaks.");
                    }

                    RejectLineBreaks(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    RejectLineBreaks(item);
                }

                break;
            case JsonValueKind.String:
                if (ContainsLineBreak(element.GetString()))
                {
                    throw new InvalidDataException("JSON strings cannot contain line breaks.");
                }

                break;
        }
    }

    private static bool ContainsLineBreak(string? value) =>
        value?.IndexOfAny(['\r', '\n', '\0']) >= 0;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdRegex();

    [GeneratedRegex("^c_[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant)]
    private static partial Regex CandidateIdRegex();
}
