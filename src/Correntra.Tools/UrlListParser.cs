namespace Correntra.Tools;

/// <summary>Classifies a URL-list entry that could not be accepted.</summary>
public enum UrlListIssueKind
{
    /// <summary>The line is not an absolute URL.</summary>
    InvalidUrl,

    /// <summary>The URL failed the configured safety policy.</summary>
    UnsafeUrl,

    /// <summary>The configured result limit was reached.</summary>
    LimitReached,
}

/// <summary>Describes a rejected URL-list line without retaining sensitive URL text.</summary>
/// <param name="LineNumber">The one-based source line number.</param>
/// <param name="Kind">The category of problem.</param>
/// <param name="SafetyReason">The more specific safety rejection, when available.</param>
public sealed record UrlListIssue(
    int LineNumber,
    UrlListIssueKind Kind,
    UrlRejectionReason SafetyReason = UrlRejectionReason.None);

/// <summary>Configures parsing and resource limits for a pasted or imported URL list.</summary>
public sealed record UrlListParseOptions
{
    /// <summary>Gets the default parsing options.</summary>
    public static UrlListParseOptions Default { get; } = new();

    /// <summary>Gets or initializes the maximum number of unique accepted URLs.</summary>
    public int MaximumUrls { get; init; } = 10_000;

    /// <summary>Gets or initializes URL safety exceptions. Internet-only URLs are accepted by default.</summary>
    public UrlSafetyPolicy SafetyPolicy { get; init; } = UrlSafetyPolicy.Strict;
}

/// <summary>Contains accepted URLs in source order and diagnostics for rejected lines.</summary>
/// <param name="Urls">Unique canonical URLs in their first-seen order.</param>
/// <param name="Issues">Line-based diagnostics that do not echo URL credentials or query values.</param>
public sealed record UrlListParseResult(
    IReadOnlyList<Uri> Urls,
    IReadOnlyList<UrlListIssue> Issues);

/// <summary>Parses newline-delimited HTTP(S) URLs with deterministic deduplication and safety checks.</summary>
public static class UrlListParser
{
    /// <summary>
    /// Parses one URL per line. Empty lines and lines beginning with <c>#</c> or <c>;</c> are ignored.
    /// Matching single or double quotes around a URL are removed.
    /// </summary>
    /// <param name="text">The pasted or imported list.</param>
    /// <param name="options">Optional limits and URL safety policy.</param>
    /// <returns>Accepted URLs and non-sensitive diagnostics.</returns>
    public static UrlListParseResult Parse(string? text, UrlListParseOptions? options = null)
    {
        options ??= UrlListParseOptions.Default;
        if (options.MaximumUrls is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumUrls,
                "MaximumUrls must be between 1 and 1,000,000.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new UrlListParseResult(Array.Empty<Uri>(), Array.Empty<UrlListIssue>());
        }

        var urls = new List<Uri>(Math.Min(options.MaximumUrls, 256));
        var issues = new List<UrlListIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        var lineNumber = 0;

        while (reader.ReadLine() is { } rawLine)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            line = TrimMatchingQuotes(line);
            if (!Uri.TryCreate(line, UriKind.Absolute, out var parsed))
            {
                issues.Add(new UrlListIssue(lineNumber, UrlListIssueKind.InvalidUrl));
                continue;
            }

            var safety = UrlGuard.Evaluate(parsed, options.SafetyPolicy);
            if (!safety.IsAllowed)
            {
                issues.Add(new UrlListIssue(lineNumber, UrlListIssueKind.UnsafeUrl, safety.Reason));
                continue;
            }

            var canonical = UrlGuard.Canonicalize(parsed);
            if (!seen.Add(canonical.AbsoluteUri))
            {
                continue;
            }

            if (urls.Count == options.MaximumUrls)
            {
                issues.Add(new UrlListIssue(lineNumber, UrlListIssueKind.LimitReached));
                break;
            }

            urls.Add(canonical);
        }

        return new UrlListParseResult(urls, issues);
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }
}
