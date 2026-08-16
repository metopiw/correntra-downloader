using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Correntra.Media.Utilities;

internal static class MediaText
{
    public static string StableId(string prefix, string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}-{Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    public static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    public static long? ParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    public static string FileExtension(Uri uri)
    {
        string path = uri.AbsolutePath;
        int dot = path.LastIndexOf('.');
        return dot >= 0 ? path[dot..].ToLowerInvariant() : string.Empty;
    }

    public static string GuessTitle(Uri sourceUri, string? candidateTitle)
    {
        if (!string.IsNullOrWhiteSpace(candidateTitle))
        {
            return candidateTitle.Trim();
        }

        string name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(sourceUri.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? sourceUri.Host : name;
    }
}

