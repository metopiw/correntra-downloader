using System.Globalization;
using Correntra.Media.Models;
using Correntra.Media.Utilities;

namespace Correntra.Media.Hls;

public sealed class HlsParser
{
    private const int MaximumLines = 1_000_000;

    public static HlsPlaylist Parse(Uri playlistUri, string content)
    {
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentNullException.ThrowIfNull(content);

        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        if (lines.Length > MaximumLines)
        {
            throw new FormatException("HLS playlist contains too many lines.");
        }

        int firstContentLine = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (firstContentLine < 0 || !string.Equals(lines[firstContentLine].TrimStart('\uFEFF').Trim(), "#EXTM3U", StringComparison.Ordinal))
        {
            throw new FormatException("The response is not an HLS playlist.");
        }

        bool isMaster = lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
        return isMaster ? ParseMaster(playlistUri, lines) : ParseMedia(playlistUri, lines);
    }

    private static HlsPlaylist ParseMaster(Uri playlistUri, IReadOnlyList<string> lines)
    {
        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        IReadOnlyDictionary<string, string>? pendingVariant = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                IReadOnlyDictionary<string, string> attributes = HlsAttributeList.Parse(AfterColon(line));
                if (!attributes.TryGetValue("TYPE", out string? type) ||
                    !attributes.TryGetValue("GROUP-ID", out string? groupId))
                {
                    continue;
                }

                attributes.TryGetValue("URI", out string? uriText);
                renditions.Add(new HlsRendition
                {
                    Type = type,
                    GroupId = groupId,
                    Name = Get(attributes, "NAME"),
                    Language = Get(attributes, "LANGUAGE"),
                    SourceUri = ResolveOptional(playlistUri, uriText),
                    IsDefault = IsYes(Get(attributes, "DEFAULT")),
                    AutoSelect = IsYes(Get(attributes, "AUTOSELECT")),
                    Forced = IsYes(Get(attributes, "FORCED")),
                    Channels = Get(attributes, "CHANNELS"),
                });
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pendingVariant = HlsAttributeList.Parse(AfterColon(line));
            }
            else if (pendingVariant is not null && line.Length > 0 && !line.StartsWith('#'))
            {
                (int? width, int? height) = ParseResolution(Get(pendingVariant, "RESOLUTION"));
                variants.Add(new HlsVariant
                {
                    SourceUri = Resolve(playlistUri, line),
                    Bandwidth = MediaText.ParseLong(Get(pendingVariant, "BANDWIDTH")),
                    AverageBandwidth = MediaText.ParseLong(Get(pendingVariant, "AVERAGE-BANDWIDTH")),
                    Width = width,
                    Height = height,
                    FrameRate = MediaText.ParseDouble(Get(pendingVariant, "FRAME-RATE")),
                    Codecs = Get(pendingVariant, "CODECS"),
                    Name = Get(pendingVariant, "NAME"),
                    AudioGroupId = Get(pendingVariant, "AUDIO"),
                    SubtitleGroupId = Get(pendingVariant, "SUBTITLES"),
                });
                pendingVariant = null;
            }
        }

        return new HlsPlaylist
        {
            SourceUri = playlistUri,
            IsMaster = true,
            IsLive = false,
            HasEndList = true,
            Variants = variants,
            Renditions = renditions,
        };
    }

    private static HlsPlaylist ParseMedia(Uri playlistUri, IReadOnlyList<string> lines)
    {
        var segments = new List<MediaSegment>();
        long mediaSequence = 0;
        TimeSpan? targetDuration = null;
        TimeSpan? pendingDuration = null;
        bool discontinuity = false;
        bool endList = false;
        long? pendingRangeLength = null;
        long? pendingRangeStart = null;
        long? previousRangeEnd = null;
        MediaEncryption? encryption = null;
        MediaSegment? initialization = null;
        MediaProtection protection = MediaProtection.None;
        string? protectionReason = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                mediaSequence = MediaText.ParseLong(AfterColon(line)) ?? 0;
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                double? seconds = MediaText.ParseDouble(AfterColon(line));
                targetDuration = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                string secondsText = AfterColon(line).Split(',', 2)[0];
                double? seconds = MediaText.ParseDouble(secondsText);
                pendingDuration = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                ParseByteRange(AfterColon(line), previousRangeEnd, out pendingRangeLength, out pendingRangeStart);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                IReadOnlyDictionary<string, string> attributes = HlsAttributeList.Parse(AfterColon(line));
                if (Get(attributes, "URI") is { Length: > 0 } uriText)
                {
                    ParseByteRange(Get(attributes, "BYTERANGE"), null, out long? mapLength, out long? mapStart);
                    initialization = new MediaSegment
                    {
                        SourceUri = Resolve(playlistUri, uriText),
                        Sequence = -1,
                        ByteRangeLength = mapLength,
                        ByteRangeStart = mapStart,
                        Encryption = encryption,
                    };
                }
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                IReadOnlyDictionary<string, string> attributes = HlsAttributeList.Parse(AfterColon(line));
                string method = Get(attributes, "METHOD") ?? "NONE";
                string? keyFormat = Get(attributes, "KEYFORMAT");

                if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    encryption = null;
                }
                else
                {
                    Uri? keyUri = ResolveOptional(playlistUri, Get(attributes, "URI"));
                    byte[]? iv = ParseIv(Get(attributes, "IV"));
                    encryption = new MediaEncryption(method, keyUri, iv, keyFormat);

                    if (string.Equals(method, "AES-128", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(keyFormat) || string.Equals(keyFormat, "identity", StringComparison.OrdinalIgnoreCase)))
                    {
                        protection = protection == MediaProtection.Drm ? protection : MediaProtection.ClearAes128;
                    }
                    else
                    {
                        protection = MediaProtection.Drm;
                        protectionReason = $"Unsupported HLS encryption method or key format: {method}/{keyFormat ?? "identity"}.";
                    }
                }
            }
            else if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
            {
                discontinuity = true;
            }
            else if (line.Equals("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                endList = true;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                long sequence = checked(mediaSequence + segments.Count);
                if (pendingRangeLength.HasValue && !pendingRangeStart.HasValue)
                {
                    pendingRangeStart = previousRangeEnd;
                }

                segments.Add(new MediaSegment
                {
                    SourceUri = Resolve(playlistUri, line),
                    Sequence = sequence,
                    Duration = pendingDuration,
                    ByteRangeLength = pendingRangeLength,
                    ByteRangeStart = pendingRangeStart,
                    Discontinuity = discontinuity,
                    Encryption = encryption,
                });

                if (pendingRangeLength.HasValue && pendingRangeStart.HasValue)
                {
                    previousRangeEnd = checked(pendingRangeStart.Value + pendingRangeLength.Value);
                }
                else
                {
                    previousRangeEnd = null;
                }

                pendingDuration = null;
                pendingRangeLength = null;
                pendingRangeStart = null;
                discontinuity = false;
            }
        }

        return new HlsPlaylist
        {
            SourceUri = playlistUri,
            IsMaster = false,
            IsLive = !endList,
            HasEndList = endList,
            MediaSequence = mediaSequence,
            TargetDuration = targetDuration,
            Segments = segments,
            InitializationSegment = initialization,
            Protection = protection,
            ProtectionReason = protectionReason,
        };
    }

    private static string AfterColon(string line)
    {
        int colon = line.IndexOf(':');
        return colon < 0 ? string.Empty : line[(colon + 1)..];
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value : null;
    }

    private static bool IsYes(string? value)
    {
        return string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri Resolve(Uri baseUri, string value)
    {
        if (!Uri.TryCreate(baseUri, value, out Uri? result) ||
            (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException("HLS playlist contains an invalid or unsupported URI.");
        }

        return result;
    }

    private static Uri? ResolveOptional(Uri baseUri, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Resolve(baseUri, value);
    }

    private static (int? Width, int? Height) ParseResolution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        string[] parts = value.Split('x', 'X');
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
            ? (width, height)
            : (null, null);
    }

    private static void ParseByteRange(string? value, long? implicitStart, out long? length, out long? start)
    {
        length = null;
        start = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value.Split('@', 2);
        length = MediaText.ParseLong(parts[0]);
        start = parts.Length == 2 ? MediaText.ParseLong(parts[1]) : implicitStart;
        if (length <= 0 || start < 0)
        {
            length = null;
            start = null;
        }
    }

    private static byte[]? ParseIv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length % 2 != 0)
        {
            hex = $"0{hex}";
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
