using System.Collections.Frozen;
using Correntra.Media.Models;
using Correntra.Media.Utilities;

namespace Correntra.Media.Sites;

public sealed record GoogleVideoInfo
{
    public int? Itag { get; init; }

    public MediaTrackKind TrackKind { get; init; }

    public string? DisplayName { get; init; }

    public string? MimeType { get; init; }

    public string? Container { get; init; }

    public string? Codecs { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public long? Bitrate { get; init; }

    public long? ContentLength { get; init; }
}

public static class GoogleVideoUrlParser
{
    private sealed record ItagProfile(MediaTrackKind Kind, string Container, int? Width, int? Height, string Label);

    private static readonly FrozenDictionary<int, ItagProfile> Profiles = new Dictionary<int, ItagProfile>
    {
        [18] = new(MediaTrackKind.Muxed, "MP4", 640, 360, "360p"),
        [22] = new(MediaTrackKind.Muxed, "MP4", 1280, 720, "720p"),
        [37] = new(MediaTrackKind.Muxed, "MP4", 1920, 1080, "1080p"),
        [133] = new(MediaTrackKind.Video, "MP4", 426, 240, "240p"),
        [134] = new(MediaTrackKind.Video, "MP4", 640, 360, "360p"),
        [135] = new(MediaTrackKind.Video, "MP4", 854, 480, "480p"),
        [136] = new(MediaTrackKind.Video, "MP4", 1280, 720, "720p"),
        [137] = new(MediaTrackKind.Video, "MP4", 1920, 1080, "1080p"),
        [138] = new(MediaTrackKind.Video, "MP4", 4096, 2160, "2160p"),
        [160] = new(MediaTrackKind.Video, "MP4", 256, 144, "144p"),
        [242] = new(MediaTrackKind.Video, "WEBM", 426, 240, "240p"),
        [243] = new(MediaTrackKind.Video, "WEBM", 640, 360, "360p"),
        [244] = new(MediaTrackKind.Video, "WEBM", 854, 480, "480p"),
        [247] = new(MediaTrackKind.Video, "WEBM", 1280, 720, "720p"),
        [248] = new(MediaTrackKind.Video, "WEBM", 1920, 1080, "1080p"),
        [271] = new(MediaTrackKind.Video, "WEBM", 2560, 1440, "1440p"),
        [272] = new(MediaTrackKind.Video, "WEBM", 3840, 2160, "2160p"),
        [298] = new(MediaTrackKind.Video, "MP4", 1280, 720, "720p60"),
        [299] = new(MediaTrackKind.Video, "MP4", 1920, 1080, "1080p60"),
        [302] = new(MediaTrackKind.Video, "WEBM", 1280, 720, "720p60"),
        [303] = new(MediaTrackKind.Video, "WEBM", 1920, 1080, "1080p60"),
        [308] = new(MediaTrackKind.Video, "WEBM", 2560, 1440, "1440p60"),
        [313] = new(MediaTrackKind.Video, "WEBM", 3840, 2160, "2160p"),
        [315] = new(MediaTrackKind.Video, "WEBM", 3840, 2160, "2160p60"),
        [139] = new(MediaTrackKind.Audio, "M4A", null, null, "48 kbps audio"),
        [140] = new(MediaTrackKind.Audio, "M4A", null, null, "128 kbps audio"),
        [141] = new(MediaTrackKind.Audio, "M4A", null, null, "256 kbps audio"),
        [249] = new(MediaTrackKind.Audio, "WEBM", null, null, "Opus audio"),
        [250] = new(MediaTrackKind.Audio, "WEBM", null, null, "Opus audio"),
        [251] = new(MediaTrackKind.Audio, "WEBM", null, null, "Opus audio"),
    }.ToFrozenDictionary();

    public static bool IsGoogleVideo(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri &&
               (uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase));
    }

    public static GoogleVideoInfo Parse(Uri uri, string? fallbackMime = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Dictionary<string, string> query = ParseQuery(uri.Query);
        int? itag = query.TryGetValue("itag", out string? itagText) && int.TryParse(itagText, out int parsedItag)
            ? parsedItag
            : null;
        Profiles.TryGetValue(itag ?? -1, out ItagProfile? profile);

        string? mime = DecodeMime(query.TryGetValue("mime", out string? mimeText) ? mimeText : fallbackMime);
        MediaTrackKind kind = profile?.Kind ?? DetermineKind(mime);
        string? container = profile?.Container ?? ContainerFromMime(mime);
        string? codecs = query.TryGetValue("codecs", out string? codecText) ? codecText : ExtractCodecs(mime);
        long? bitrate = MediaText.ParseLong(query.TryGetValue("bitrate", out string? bitrateText) ? bitrateText : null);
        long? contentLength = MediaText.ParseLong(query.TryGetValue("clen", out string? lengthText) ? lengthText : null);

        return new GoogleVideoInfo
        {
            Itag = itag,
            TrackKind = kind,
            DisplayName = profile?.Label ?? (itag is null ? "YouTube stream" : $"YouTube itag {itag}"),
            MimeType = MimeOnly(mime),
            Container = container,
            Codecs = codecs,
            Width = profile?.Width,
            Height = profile?.Height,
            Bitrate = bitrate,
            ContentLength = contentLength,
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = item.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            result.TryAdd(key, value);
        }

        return result;
    }

    private static string? DecodeMime(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value);
    }

    private static MediaTrackKind DetermineKind(string? mime)
    {
        string value = MimeOnly(mime) ?? string.Empty;
        return value.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            ? MediaTrackKind.Video
            : value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                ? MediaTrackKind.Audio
                : MediaTrackKind.Unknown;
    }

    private static string? ContainerFromMime(string? mime)
    {
        string? bare = MimeOnly(mime);
        if (bare?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true)
        {
            return bare.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "M4A" : "MP4";
        }

        return bare?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true ? "WEBM" : null;
    }

    private static string? MimeOnly(string? mime)
    {
        return mime?.Split(';', 2)[0].Trim();
    }

    private static string? ExtractCodecs(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return null;
        }

        int marker = mime.IndexOf("codecs=", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? null : mime[(marker + 7)..].Trim().Trim('"');
    }
}
