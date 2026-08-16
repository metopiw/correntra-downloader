using System.Text;

namespace Correntra.Platform.Windows.Files;

public static class ZoneIdentifierWriter
{
    public static async Task MarkFromInternetAsync(
        string filePath,
        Uri sourceUri,
        Uri? referrerUri = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (!sourceUri.IsAbsoluteUri || sourceUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The source URL must use HTTP or HTTPS.", nameof(sourceUri));
        }

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The downloaded file was not found.", fullPath);
        }

        string streamPath = fullPath + ":Zone.Identifier";
        string source = WithoutCredentials(sourceUri);
        string? referrer = referrerUri is null ? null : WithoutCredentials(referrerUri);
        string content = $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={source}\r\n" +
                         (referrer is null ? string.Empty : $"ReferrerUrl={referrer}\r\n");
        await File.WriteAllTextAsync(streamPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static string WithoutCredentials(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }
}

