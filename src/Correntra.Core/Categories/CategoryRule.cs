using System.Collections.Immutable;
using Correntra.Core.Internal;

namespace Correntra.Core.Categories;

public sealed class CategoryMatchContext
{
    public CategoryMatchContext(Uri sourceUrl, string? fileName = null, string? contentType = null)
    {
        SourceUrl = Guard.HttpUri(sourceUrl, nameof(sourceUrl));
        FileName = string.IsNullOrWhiteSpace(fileName)
            ? null
            : Guard.NotNullOrWhiteSpace(fileName, nameof(fileName), 2_000);
        FileExtension = ExtractExtension(FileName);
        ContentType = NormalizeContentType(contentType);
    }

    public Uri SourceUrl { get; }

    public string? FileName { get; }

    public string? FileExtension { get; }

    public string? ContentType { get; }

    private static string? ExtractExtension(string? fileName)
    {
        if (fileName is null)
        {
            return null;
        }

        int queryIndex = fileName.IndexOfAny(['?', '#']);
        string cleanName = queryIndex < 0 ? fileName : fileName[..queryIndex];
        int separatorIndex = cleanName.LastIndexOfAny(['/', '\\']);
        int dotIndex = cleanName.LastIndexOf('.');
        if (dotIndex <= separatorIndex || dotIndex == cleanName.Length - 1)
        {
            return null;
        }

        string extension = cleanName[dotIndex..];
        return extension.Length <= 32 ? extension.ToLowerInvariant() : null;
    }

    private static string? NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string mediaType = value.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType.Length is > 0 and <= 200 ? mediaType : null;
    }
}

public sealed class CategoryRule
{
    public CategoryRule(
        CategoryRuleId id,
        CategoryId categoryId,
        int priority,
        string? sitePattern = null,
        IEnumerable<string>? fileExtensions = null,
        IEnumerable<string>? contentTypePrefixes = null,
        bool isEnabled = true)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A category rule ID cannot be empty.", nameof(id));
        }

        if (categoryId.IsEmpty)
        {
            throw new ArgumentException("A category ID cannot be empty.", nameof(categoryId));
        }

        if (priority is < -10_000 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        Id = id;
        CategoryId = categoryId;
        Priority = priority;
        SitePattern = NormalizeSitePattern(sitePattern);
        FileExtensions = NormalizeExtensions(fileExtensions, nameof(fileExtensions));
        ContentTypePrefixes = NormalizeContentTypes(contentTypePrefixes, nameof(contentTypePrefixes));
        IsEnabled = isEnabled;

        if (SitePattern is null && FileExtensions.Count == 0 && ContentTypePrefixes.Count == 0)
        {
            throw new ArgumentException("A category rule must contain at least one matching criterion.");
        }
    }

    public CategoryRuleId Id { get; }

    public CategoryId CategoryId { get; }

    public int Priority { get; }

    public string? SitePattern { get; }

    public ImmutableHashSet<string> FileExtensions { get; }

    public ImmutableHashSet<string> ContentTypePrefixes { get; }

    public bool IsEnabled { get; }

    public bool Matches(CategoryMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEnabled)
        {
            return false;
        }

        if (SitePattern is { } pattern && !HostMatches(context.SourceUrl.IdnHost, pattern))
        {
            return false;
        }

        if (FileExtensions.Count > 0 &&
            (context.FileExtension is null || !FileExtensions.Contains(context.FileExtension)))
        {
            return false;
        }

        if (ContentTypePrefixes.Count > 0 &&
            (context.ContentType is null ||
             !ContentTypePrefixes.Any(prefix => context.ContentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return true;
    }

    internal static ImmutableHashSet<string> NormalizeExtensions(
        IEnumerable<string>? values,
        string parameterName)
    {
        ImmutableHashSet<string>.Builder builder =
            ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        if (values is null)
        {
            return builder.ToImmutable();
        }

        foreach (string? value in values)
        {
            string extension = Guard.NotNullOrWhiteSpace(value, parameterName, 32).ToLowerInvariant();
            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            if (extension.Length < 2 || extension.Skip(1).Any(static character => !char.IsLetterOrDigit(character)))
            {
                throw new ArgumentException("A file extension contains an invalid character.", parameterName);
            }

            builder.Add(extension);
        }

        return builder.ToImmutable();
    }

    internal static ImmutableHashSet<string> NormalizeContentTypes(
        IEnumerable<string>? values,
        string parameterName)
    {
        ImmutableHashSet<string>.Builder builder =
            ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        if (values is null)
        {
            return builder.ToImmutable();
        }

        foreach (string? value in values)
        {
            string prefix = Guard.NotNullOrWhiteSpace(value, parameterName, 200).ToLowerInvariant();
            if (prefix.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
                !prefix.Contains('/'))
            {
                throw new ArgumentException("A content type prefix is invalid.", parameterName);
            }

            builder.Add(prefix);
        }

        return builder.ToImmutable();
    }

    private static string? NormalizeSitePattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string pattern = Guard.NotNullOrWhiteSpace(value, nameof(value), 253)
            .TrimEnd('.')
            .ToLowerInvariant();
        string host = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;

        if (host.Length == 0 ||
            host.Split('.').Any(static label =>
                label.Length is < 1 or > 63 ||
                label.StartsWith('-') ||
                label.EndsWith('-') ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            throw new ArgumentException("A site pattern must be a valid ASCII host or wildcard subdomain.", nameof(value));
        }

        return pattern;
    }

    private static bool HostMatches(string host, string pattern)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
        }

        string suffix = pattern[1..];
        return host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
