using System.Globalization;
using System.Text;

namespace Correntra.Tools;

/// <summary>Reports a malformed or unsafe batch URL pattern.</summary>
public sealed class BatchPatternException : FormatException
{
    /// <summary>Initializes an exception with a user-safe explanation.</summary>
    /// <param name="message">The explanation.</param>
    public BatchPatternException(string message)
        : base(message)
    {
    }
}

/// <summary>Represents one variable position in a wildcard URL.</summary>
public abstract class BatchAxis
{
    /// <summary>Gets the number of values produced by this axis.</summary>
    public abstract int Count { get; }

    internal abstract string GetValue(int index);
}

/// <summary>Produces an inclusive ascending or descending integer sequence.</summary>
public sealed class NumericBatchAxis : BatchAxis
{
    /// <summary>Initializes a numeric axis.</summary>
    /// <param name="start">The first non-negative value.</param>
    /// <param name="end">The inclusive last non-negative value.</param>
    /// <param name="width">Zero for natural formatting, or a fixed zero-padded width.</param>
    /// <param name="step">The positive distance between values.</param>
    public NumericBatchAxis(int start, int end, int width = 0, int step = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 18);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);

        var requiredWidth = Math.Max(
            start.ToString(CultureInfo.InvariantCulture).Length,
            end.ToString(CultureInfo.InvariantCulture).Length);
        if (width != 0 && width < requiredWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width cannot truncate a value.");
        }

        Start = start;
        End = end;
        Width = width;
        Step = step;
        Count = checked((Math.Abs((long)end - start) / step) + 1) is var count && count <= int.MaxValue
            ? (int)count
            : throw new ArgumentOutOfRangeException(nameof(end), "The range is too large.");
    }

    /// <summary>Gets the first value.</summary>
    public int Start { get; }

    /// <summary>Gets the inclusive final boundary.</summary>
    public int End { get; }

    /// <summary>Gets the fixed zero-padded width, or zero for natural formatting.</summary>
    public int Width { get; }

    /// <summary>Gets the positive distance between values.</summary>
    public int Step { get; }

    /// <inheritdoc />
    public override int Count { get; }

    internal override string GetValue(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        var direction = End >= Start ? 1L : -1L;
        var value = Start + (direction * index * Step);
        return Width == 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : value.ToString($"D{Width}", CultureInfo.InvariantCulture);
    }
}

/// <summary>Produces an inclusive ascending or descending ASCII letter sequence.</summary>
public sealed class AlphabeticBatchAxis : BatchAxis
{
    /// <summary>Initializes an alphabetic axis.</summary>
    /// <param name="start">The first ASCII letter.</param>
    /// <param name="end">The inclusive last ASCII letter in the same case.</param>
    /// <param name="step">The positive distance between letters.</param>
    public AlphabeticBatchAxis(char start, char end, int step = 1)
    {
        if (!IsAsciiLetter(start) || !IsAsciiLetter(end) || char.IsUpper(start) != char.IsUpper(end))
        {
            throw new ArgumentException("Alphabetic axes require ASCII letters with matching case.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);

        Start = start;
        End = end;
        Step = step;
        Count = (Math.Abs(end - start) / step) + 1;
    }

    /// <summary>Gets the first letter.</summary>
    public char Start { get; }

    /// <summary>Gets the inclusive final boundary.</summary>
    public char End { get; }

    /// <summary>Gets the positive distance between letters.</summary>
    public int Step { get; }

    /// <inheritdoc />
    public override int Count { get; }

    internal override string GetValue(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        var direction = End >= Start ? 1 : -1;
        return ((char)(Start + (direction * index * Step))).ToString();
    }

    private static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}

/// <summary>Controls batch expansion limits and destination safety.</summary>
public sealed record BatchExpansionOptions
{
    /// <summary>Gets the default expansion options.</summary>
    public static BatchExpansionOptions Default { get; } = new();

    /// <summary>Gets or initializes the maximum number of generated URLs.</summary>
    public int MaximumResults { get; init; } = 10_000;

    /// <summary>Gets or initializes URL safety exceptions. Internet-only URLs are accepted by default.</summary>
    public UrlSafetyPolicy SafetyPolicy { get; init; } = UrlSafetyPolicy.Strict;
}

/// <summary>Expands IDM-style numeric and alphabetic ranges into safe, deterministic URL lists.</summary>
public static class BatchPatternExpander
{
    /// <summary>
    /// Expands inline ranges such as <c>image_[001-100].jpg</c>, <c>part_[a-f]</c>, and
    /// stepped ranges such as <c>[010-100:10]</c>. Multiple ranges form a Cartesian product.
    /// </summary>
    /// <param name="pattern">An absolute HTTP(S) URL containing zero or more ranges.</param>
    /// <param name="options">Optional limits and URL safety policy.</param>
    /// <returns>Canonical URLs in predictable range order.</returns>
    public static IReadOnlyList<Uri> ExpandInline(
        string pattern,
        BatchExpansionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var segments = new List<string>();
        var axes = new List<BatchAxis>();
        var cursor = 0;
        var literalStart = 0;

        while (cursor < pattern.Length)
        {
            if (pattern[cursor] != '[')
            {
                cursor++;
                continue;
            }

            var closing = pattern.IndexOf(']', cursor + 1);
            if (closing < 0)
            {
                break;
            }

            var token = pattern[(cursor + 1)..closing];
            if (!TryParseAxis(token, out var axis))
            {
                cursor = closing + 1;
                continue;
            }

            segments.Add(pattern[literalStart..cursor]);
            axes.Add(axis);
            cursor = closing + 1;
            literalStart = cursor;
        }

        segments.Add(pattern[literalStart..]);
        return ExpandSegments(segments, axes, options);
    }

    /// <summary>Replaces each <c>*</c> in a URL pattern with the corresponding axis.</summary>
    /// <param name="pattern">An absolute HTTP(S) URL with one asterisk for each axis.</param>
    /// <param name="axes">The numeric or alphabetic axes, from left to right.</param>
    /// <param name="options">Optional limits and URL safety policy.</param>
    /// <returns>Canonical URLs in predictable Cartesian-product order.</returns>
    public static IReadOnlyList<Uri> ExpandWildcards(
        string pattern,
        IReadOnlyList<BatchAxis> axes,
        BatchExpansionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(axes);

        var segments = pattern.Split('*');
        if (segments.Length - 1 != axes.Count)
        {
            throw new BatchPatternException("The number of '*' placeholders must match the number of axes.");
        }

        if (axes.Any(static axis => axis is null))
        {
            throw new ArgumentException("Axes cannot contain null values.", nameof(axes));
        }

        return ExpandSegments(segments, axes, options);
    }

    private static List<Uri> ExpandSegments(
        IReadOnlyList<string> segments,
        IReadOnlyList<BatchAxis> axes,
        BatchExpansionOptions? options)
    {
        options ??= BatchExpansionOptions.Default;
        if (options.MaximumResults is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumResults,
                "MaximumResults must be between 1 and 1,000,000.");
        }

        long total = 1;
        foreach (var axis in axes)
        {
            total = checked(total * axis.Count);
            if (total > options.MaximumResults)
            {
                throw new BatchPatternException("The batch pattern exceeds the configured result limit.");
            }
        }

        var results = new List<Uri>((int)total);
        for (var combination = 0L; combination < total; combination++)
        {
            var indexes = new int[axes.Count];
            var remainder = combination;
            for (var axisIndex = axes.Count - 1; axisIndex >= 0; axisIndex--)
            {
                indexes[axisIndex] = (int)(remainder % axes[axisIndex].Count);
                remainder /= axes[axisIndex].Count;
            }

            var builder = new StringBuilder(segments.Sum(static segment => segment.Length) + (axes.Count * 8));
            builder.Append(segments[0]);
            for (var axisIndex = 0; axisIndex < axes.Count; axisIndex++)
            {
                builder.Append(axes[axisIndex].GetValue(indexes[axisIndex]));
                builder.Append(segments[axisIndex + 1]);
            }

            if (!Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var url))
            {
                throw new BatchPatternException("The expanded pattern did not produce a valid absolute URL.");
            }

            var safety = UrlGuard.Evaluate(url, options.SafetyPolicy);
            if (!safety.IsAllowed)
            {
                throw new BatchPatternException($"The expanded URL was rejected: {safety.Reason}.");
            }

            results.Add(UrlGuard.Canonicalize(url));
        }

        return results;
    }

    private static bool TryParseAxis(string token, out BatchAxis axis)
    {
        axis = null!;
        var separator = token.IndexOf('-');
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        var stepSeparator = token.IndexOf(':', separator + 1);
        var startText = token[..separator];
        var endText = stepSeparator < 0 ? token[(separator + 1)..] : token[(separator + 1)..stepSeparator];
        var stepText = stepSeparator < 0 ? null : token[(stepSeparator + 1)..];

        var step = 1;
        if (stepText is not null
            && (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
        {
            throw new BatchPatternException("A range step must be a positive integer.");
        }

        if (int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var numericStart)
            && int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var numericEnd))
        {
            var padded = (startText.Length > 1 && startText[0] == '0')
                || (endText.Length > 1 && endText[0] == '0');
            var width = padded ? Math.Max(startText.Length, endText.Length) : 0;
            axis = new NumericBatchAxis(numericStart, numericEnd, width, step);
            return true;
        }

        if (startText.Length == 1 && endText.Length == 1
            && IsAsciiLetter(startText[0]) && IsAsciiLetter(endText[0]))
        {
            axis = new AlphabeticBatchAxis(startText[0], endText[0], step);
            return true;
        }

        return false;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
