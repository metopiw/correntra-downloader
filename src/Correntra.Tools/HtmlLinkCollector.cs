using System.Net;

namespace Correntra.Tools;

/// <summary>Identifies where an extracted URL appeared in the HTML document.</summary>
public enum CollectedLinkKind
{
    /// <summary>A URL from an <c>href</c> attribute.</summary>
    Hyperlink,

    /// <summary>A URL from a general <c>src</c> attribute.</summary>
    Source,

    /// <summary>A media URL from an audio, video, or source element.</summary>
    MediaSource,

    /// <summary>A candidate from a responsive <c>srcset</c> attribute.</summary>
    SourceSet,

    /// <summary>A video poster image.</summary>
    Poster,
}

/// <summary>Represents one unique URL extracted from an HTML document.</summary>
/// <param name="Url">The canonical absolute URL.</param>
/// <param name="Kind">The source attribute category.</param>
/// <param name="ElementName">The lower-case HTML element name.</param>
/// <param name="AttributeName">The lower-case source attribute name.</param>
public sealed record CollectedLink(
    Uri Url,
    CollectedLinkKind Kind,
    string ElementName,
    string AttributeName);

/// <summary>Configures safe HTML link extraction and user-facing filters.</summary>
public sealed record HtmlLinkCollectorOptions
{
    /// <summary>Gets the default collection options.</summary>
    public static HtmlLinkCollectorOptions Default { get; } = new();

    /// <summary>Gets or initializes accepted path suffixes, such as <c>.zip</c> or <c>.tar.gz</c>.</summary>
    public IReadOnlyCollection<string> AllowedExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Gets or initializes case-insensitive glob patterns that an absolute URL must match.</summary>
    public IReadOnlyCollection<string> IncludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Gets or initializes case-insensitive glob patterns that reject an absolute URL.</summary>
    public IReadOnlyCollection<string> ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Gets or initializes whether results must use the document host.</summary>
    public bool SameHostOnly { get; init; }

    /// <summary>Gets or initializes the maximum number of unique links returned.</summary>
    public int MaximumResults { get; init; } = 10_000;

    /// <summary>Gets or initializes the maximum accepted HTML character count.</summary>
    public int MaximumHtmlCharacters { get; init; } = 5_000_000;

    /// <summary>Gets or initializes URL safety exceptions. Internet-only URLs are accepted by default.</summary>
    public UrlSafetyPolicy SafetyPolicy { get; init; } = UrlSafetyPolicy.Strict;
}

/// <summary>Contains HTML collection results in first-occurrence order.</summary>
/// <param name="Links">Unique links in deterministic document order.</param>
/// <param name="EffectiveBaseUrl">The document URL or the first accepted HTML base URL.</param>
/// <param name="WasTruncated">Whether another matching unique result existed beyond the limit.</param>
public sealed record HtmlLinkCollectionResult(
    IReadOnlyList<CollectedLink> Links,
    Uri EffectiveBaseUrl,
    bool WasTruncated);

/// <summary>
/// Extracts link and media attributes with a small, non-executing HTML tokenizer. Script and style bodies
/// are skipped, so JavaScript strings and CSS declarations are never mistaken for document links.
/// </summary>
public static class HtmlLinkCollector
{
    /// <summary>Collects safe absolute links from HTML.</summary>
    /// <param name="html">The HTML source; it is parsed but never executed.</param>
    /// <param name="documentUrl">The absolute response URL used for relative resolution.</param>
    /// <param name="options">Optional filters, safety exceptions, and limits.</param>
    /// <returns>Unique links, effective base URL, and truncation state.</returns>
    public static HtmlLinkCollectionResult Collect(
        string html,
        Uri documentUrl,
        HtmlLinkCollectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(documentUrl);
        options ??= HtmlLinkCollectorOptions.Default;
        ValidateOptions(options);

        var documentSafety = UrlGuard.Evaluate(documentUrl, options.SafetyPolicy);
        if (!documentSafety.IsAllowed)
        {
            throw new ArgumentException($"The document URL was rejected: {documentSafety.Reason}.", nameof(documentUrl));
        }

        if (html.Length > options.MaximumHtmlCharacters)
        {
            throw new ArgumentException("The HTML document exceeds the configured character limit.", nameof(html));
        }

        var canonicalDocumentUrl = UrlGuard.Canonicalize(documentUrl);
        var effectiveBaseUrl = FindEffectiveBaseUrl(html, canonicalDocumentUrl, options.SafetyPolicy);
        var links = new List<CollectedLink>(Math.Min(options.MaximumResults, 256));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var normalizedExtensions = NormalizeExtensions(options.AllowedExtensions);

        ScanTags(html, (elementName, attributes) =>
        {
            if (attributes.TryGetValue("href", out var href))
            {
                AddCandidate(href, CollectedLinkKind.Hyperlink, elementName, "href");
            }

            if (attributes.TryGetValue("src", out var source))
            {
                var kind = elementName is "audio" or "video" or "source"
                    ? CollectedLinkKind.MediaSource
                    : CollectedLinkKind.Source;
                AddCandidate(source, kind, elementName, "src");
            }

            if (attributes.TryGetValue("srcset", out var sourceSet))
            {
                foreach (var candidate in ParseSourceSet(sourceSet))
                {
                    AddCandidate(candidate, CollectedLinkKind.SourceSet, elementName, "srcset");
                }
            }

            if (elementName == "video" && attributes.TryGetValue("poster", out var poster))
            {
                AddCandidate(poster, CollectedLinkKind.Poster, elementName, "poster");
            }

            return !truncated;
        });

        return new HtmlLinkCollectionResult(links, effectiveBaseUrl, truncated);

        void AddCandidate(string rawValue, CollectedLinkKind kind, string elementName, string attributeName)
        {
            var decoded = WebUtility.HtmlDecode(rawValue).Trim();
            if (decoded.Length == 0 || decoded[0] == '#'
                || !Uri.TryCreate(effectiveBaseUrl, decoded, out var resolved))
            {
                return;
            }

            var safety = UrlGuard.Evaluate(resolved, options.SafetyPolicy);
            if (!safety.IsAllowed)
            {
                return;
            }

            var canonical = UrlGuard.Canonicalize(resolved);
            if (options.SameHostOnly
                && !canonical.IdnHost.Equals(canonicalDocumentUrl.IdnHost, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!MatchesFilters(canonical, normalizedExtensions, options.IncludePatterns, options.ExcludePatterns))
            {
                return;
            }

            if (!seen.Add(canonical.AbsoluteUri))
            {
                return;
            }

            if (links.Count == options.MaximumResults)
            {
                truncated = true;
                return;
            }

            links.Add(new CollectedLink(canonical, kind, elementName, attributeName));
        }
    }

    private static void ValidateOptions(HtmlLinkCollectorOptions options)
    {
        if (options.MaximumResults is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumResults must be between 1 and 1,000,000.");
        }

        if (options.MaximumHtmlCharacters is < 1 or > 50_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumHtmlCharacters must be between 1 and 50,000,000.");
        }

        if (options.AllowedExtensions.Any(static value => string.IsNullOrWhiteSpace(value))
            || options.IncludePatterns.Any(static value => string.IsNullOrWhiteSpace(value))
            || options.ExcludePatterns.Any(static value => string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Filter collections cannot contain blank values.", nameof(options));
        }
    }

    private static Uri FindEffectiveBaseUrl(string html, Uri documentUrl, UrlSafetyPolicy policy)
    {
        Uri? baseUrl = null;
        ScanTags(html, (elementName, attributes) =>
        {
            if (elementName != "base" || !attributes.TryGetValue("href", out var value))
            {
                return true;
            }

            var decoded = WebUtility.HtmlDecode(value).Trim();
            if (Uri.TryCreate(documentUrl, decoded, out var resolved)
                && UrlGuard.Evaluate(resolved, policy).IsAllowed)
            {
                baseUrl = UrlGuard.Canonicalize(resolved);
                return false;
            }

            return true;
        });

        return baseUrl ?? documentUrl;
    }

    private static string[] NormalizeExtensions(IReadOnlyCollection<string> extensions) =>
        extensions
            .Select(static extension => extension.Trim())
            .Select(static extension => extension[0] == '.' ? extension : $".{extension}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool MatchesFilters(
        Uri url,
        IReadOnlyCollection<string> extensions,
        IReadOnlyCollection<string> includes,
        IReadOnlyCollection<string> excludes)
    {
        if (extensions.Count != 0
            && !extensions.Any(extension => url.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var absolute = url.AbsoluteUri;
        if (excludes.Any(pattern => GlobMatches(absolute, pattern)))
        {
            return false;
        }

        return includes.Count == 0 || includes.Any(pattern => GlobMatches(absolute, pattern));
    }

    private static bool GlobMatches(string value, string pattern)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var retryValueIndex = -1;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static IEnumerable<string> ParseSourceSet(string sourceSet)
    {
        foreach (var part in sourceSet.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var whitespace = trimmed.IndexOfAny([' ', '\t', '\r', '\n', '\f']);
            yield return whitespace < 0 ? trimmed : trimmed[..whitespace];
        }
    }

    private static void ScanTags(
        string html,
        Func<string, IReadOnlyDictionary<string, string>, bool> visitor)
    {
        var cursor = 0;
        while (cursor < html.Length)
        {
            var opening = html.IndexOf('<', cursor);
            if (opening < 0 || opening + 1 >= html.Length)
            {
                return;
            }

            cursor = opening + 1;
            if (html.AsSpan(cursor).StartsWith("!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", cursor + 3, StringComparison.Ordinal);
                cursor = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }

            if (html[cursor] is '!' or '?' or '/')
            {
                cursor = FindTagEnd(html, cursor + 1);
                continue;
            }

            var nameStart = cursor;
            while (cursor < html.Length && IsNameCharacter(html[cursor]))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                continue;
            }

            var elementName = html[nameStart..cursor].ToLowerInvariant();
            var attributes = ParseAttributes(html, ref cursor);
            if (!visitor(elementName, attributes))
            {
                return;
            }

            if (elementName is "script" or "style")
            {
                var closing = html.IndexOf($"</{elementName}", cursor, StringComparison.OrdinalIgnoreCase);
                cursor = closing < 0 ? html.Length : FindTagEnd(html, closing + elementName.Length + 2);
            }
        }
    }

    private static Dictionary<string, string> ParseAttributes(string html, ref int cursor)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (cursor < html.Length)
        {
            SkipWhitespace(html, ref cursor);
            if (cursor >= html.Length)
            {
                break;
            }

            if (html[cursor] == '>')
            {
                cursor++;
                break;
            }

            if (html[cursor] == '/' && cursor + 1 < html.Length && html[cursor + 1] == '>')
            {
                cursor += 2;
                break;
            }

            var nameStart = cursor;
            while (cursor < html.Length && IsAttributeNameCharacter(html[cursor]))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            var attributeName = html[nameStart..cursor].ToLowerInvariant();
            SkipWhitespace(html, ref cursor);
            var value = string.Empty;
            if (cursor < html.Length && html[cursor] == '=')
            {
                cursor++;
                SkipWhitespace(html, ref cursor);
                value = ParseAttributeValue(html, ref cursor);
            }

            attributes.TryAdd(attributeName, value);
        }

        return attributes;
    }

    private static string ParseAttributeValue(string html, ref int cursor)
    {
        if (cursor >= html.Length)
        {
            return string.Empty;
        }

        var quote = html[cursor];
        if (quote is '"' or '\'')
        {
            cursor++;
            var start = cursor;
            while (cursor < html.Length && html[cursor] != quote)
            {
                cursor++;
            }

            var value = html[start..cursor];
            if (cursor < html.Length)
            {
                cursor++;
            }

            return value;
        }

        var valueStart = cursor;
        while (cursor < html.Length && !char.IsWhiteSpace(html[cursor]) && html[cursor] != '>')
        {
            cursor++;
        }

        return html[valueStart..cursor];
    }

    private static int FindTagEnd(string html, int cursor)
    {
        char? quote = null;
        while (cursor < html.Length)
        {
            var current = html[cursor++];
            if (quote is not null)
            {
                if (current == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '>')
            {
                break;
            }
        }

        return cursor;
    }

    private static void SkipWhitespace(string html, ref int cursor)
    {
        while (cursor < html.Length && char.IsWhiteSpace(html[cursor]))
        {
            cursor++;
        }
    }

    private static bool IsNameCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is ':' or '-' or '_';

    private static bool IsAttributeNameCharacter(char value) =>
        !char.IsWhiteSpace(value) && value is not '=' and not '>' and not '/' and not '<';
}
