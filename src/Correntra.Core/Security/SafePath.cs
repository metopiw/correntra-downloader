using System.Globalization;
using System.Text;
using Correntra.Core.Internal;

namespace Correntra.Core.Security;

public static class SafePath
{
    public const int DefaultMaximumComponentLength = 240;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "COM¹",
        "COM²",
        "COM³",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
        "LPT¹",
        "LPT²",
        "LPT³",
    };

    public static bool IsValidComponent(string? value, int maximumLength = DefaultMaximumComponentLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumLength < 1 || value.Length > maximumLength)
        {
            return false;
        }

        if (!string.Equals(value, value.Normalize(NormalizationForm.FormC), StringComparison.Ordinal) ||
            value is "." or ".." ||
            value.EndsWith(' ') ||
            value.EndsWith('.'))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return false;
            }
        }

        return !IsWindowsReservedName(value);
    }

    public static string ValidateComponent(
        string? value,
        string parameterName = "value",
        int maximumLength = DefaultMaximumComponentLength)
    {
        if (!IsValidComponent(value, maximumLength))
        {
            throw new ArgumentException("The value is not a safe file-system path component.", parameterName);
        }

        return value!;
    }

    public static string SanitizeFileName(
        string? value,
        string fallback = "download",
        int maximumLength = DefaultMaximumComponentLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);

        string safeFallback = ValidateComponent(fallback, nameof(fallback), maximumLength);
        if (string.IsNullOrWhiteSpace(value))
        {
            return safeFallback;
        }

        string normalized = value.Normalize(NormalizationForm.FormC);
        StringBuilder builder = new(normalized.Length);
        bool previousWasReplacement = false;

        foreach (char character in normalized)
        {
            bool invalid = char.IsControl(character) ||
                character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

            if (invalid)
            {
                if (!previousWasReplacement)
                {
                    builder.Append('_');
                }

                previousWasReplacement = true;
                continue;
            }

            builder.Append(character);
            previousWasReplacement = false;
        }

        string candidate = builder.ToString().Trim().TrimEnd('.', ' ');
        if (candidate is "." or ".." || candidate.Length == 0)
        {
            candidate = safeFallback;
        }

        if (IsWindowsReservedName(candidate))
        {
            candidate = "_" + candidate;
        }

        candidate = TruncatePreservingExtension(candidate, maximumLength);
        candidate = candidate.TrimEnd('.', ' ');

        return IsValidComponent(candidate, maximumLength) ? candidate : safeFallback;
    }

    public static string CanonicalizeDirectory(string? path, string parameterName = "path")
    {
        string value = Guard.NotNullOrWhiteSpace(path, parameterName, short.MaxValue);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("A path cannot contain a null character.", parameterName);
        }

        string fullPath = Path.GetFullPath(value);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("A fully-qualified directory is required.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string CombineUnderRoot(string? approvedRoot, params string[] components)
    {
        string root = CanonicalizeDirectory(approvedRoot, nameof(approvedRoot));
        ArgumentNullException.ThrowIfNull(components);

        string candidate = root;
        foreach (string component in components)
        {
            candidate = Path.Combine(candidate, ValidateComponent(component, nameof(components)));
        }

        candidate = Path.GetFullPath(candidate);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, comparison))
        {
            throw new ArgumentException("The resulting path escapes the approved root.", nameof(components));
        }

        return candidate;
    }

    private static bool IsWindowsReservedName(string value)
    {
        int dotIndex = value.IndexOf('.');
        string stem = dotIndex >= 0 ? value[..dotIndex] : value;
        return WindowsReservedNames.Contains(stem.TrimEnd(' ', '.'));
    }

    private static string TruncatePreservingExtension(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        string extension = Path.GetExtension(value);
        if (extension.Length is > 1 and < 32 && extension.Length < maximumLength)
        {
            int stemLength = maximumLength - extension.Length;
            return value[..stemLength].TrimEnd('.', ' ') + extension;
        }

        return value[..maximumLength];
    }
}
