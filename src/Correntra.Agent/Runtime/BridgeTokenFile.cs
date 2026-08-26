using System.Security.Cryptography;
using System.Text;

namespace Correntra.Agent.Runtime;

/// <summary>
/// Shared secret for the loopback HTTP bridge. A fresh token is generated on
/// every agent start and written into the deployed <c>browser-extension/</c>
/// folder, where only the extension's own service worker can read it (its ID
/// is pinned; web pages cannot fetch chrome-extension:// resources). Requests
/// presenting the wrong token get 401, which closes the residual gap left by
/// Origin checking alone: other local processes forging an Origin header.
/// </summary>
public static class BridgeTokenFile
{
    public const string FileName = "bridge-token.txt";

    /// <summary>Creates a 256-bit URL-safe token.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Writes the token atomically into <paramref name="directory"/> so the
    /// extension service worker can read it via its own origin.
    /// </summary>
    public static bool TryWrite(string directory, string token)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, token + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Constant-time comparison; never throws on malformed input.</summary>
    public static bool IsValid(string? candidate, string expected)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidate);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }
}
