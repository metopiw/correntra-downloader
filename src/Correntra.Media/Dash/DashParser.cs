using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Correntra.Media.Models;
using Correntra.Media.Utilities;

namespace Correntra.Media.Dash;

public sealed class DashParser
{
    private const int MaximumManifestCharacters = 16 * 1024 * 1024;
    private const int MaximumExpandedSegments = 250_000;

    public static DashManifest Parse(Uri manifestUri, string content)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length > MaximumManifestCharacters)
        {
            throw new FormatException("DASH manifest exceeds the supported size limit.");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(content, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new FormatException("The response is not a valid DASH manifest.", exception);
        }

        XElement root = document.Root ?? throw new FormatException("DASH manifest has no root element.");
        if (!string.Equals(root.Name.LocalName, "MPD", StringComparison.Ordinal))
        {
            throw new FormatException("The response is not a DASH MPD document.");
        }

        bool isDynamic = string.Equals(Attribute(root, "type"), "dynamic", StringComparison.OrdinalIgnoreCase);
        TimeSpan? totalDuration = ParseDuration(Attribute(root, "mediaPresentationDuration"));
        Uri rootBase = ResolveBase(manifestUri, ChildValue(root, "BaseURL"));
        MediaProtection rootProtection = DetectProtection(root, out string? protectionReason);
        var representations = new List<DashRepresentation>();

        IEnumerable<XElement> periods = Children(root, "Period");
        foreach (XElement period in periods)
        {
            Uri periodBase = ResolveBase(rootBase, ChildValue(period, "BaseURL"));
            TimeSpan? periodDuration = ParseDuration(Attribute(period, "duration")) ?? totalDuration;

            foreach (XElement adaptation in Children(period, "AdaptationSet"))
            {
                Uri adaptationBase = ResolveBase(periodBase, ChildValue(adaptation, "BaseURL"));
                MediaProtection adaptationProtection = MaxProtection(rootProtection, DetectProtection(adaptation, out string? adaptationReason));
                protectionReason ??= adaptationReason;

                foreach (XElement representation in Children(adaptation, "Representation"))
                {
                    Uri representationBase = ResolveBase(adaptationBase, ChildValue(representation, "BaseURL"));
                    string id = Attribute(representation, "id") ??
                        MediaText.StableId("dash", representationBase.AbsoluteUri);
                    string? mime = Attribute(representation, "mimeType") ?? Attribute(adaptation, "mimeType");
                    string? contentType = Attribute(representation, "contentType") ?? Attribute(adaptation, "contentType");
                    string? codecs = Attribute(representation, "codecs") ?? Attribute(adaptation, "codecs");
                    string? language = Attribute(representation, "lang") ?? Attribute(adaptation, "lang");
                    MediaTrackKind trackKind = DetermineTrackKind(contentType, mime);
                    MediaProtection representationProtection = MaxProtection(
                        adaptationProtection,
                        DetectProtection(representation, out string? representationReason));
                    protectionReason ??= representationReason;

                    XElement? segmentTemplate = Child(representation, "SegmentTemplate") ??
                        Child(adaptation, "SegmentTemplate") ??
                        Child(period, "SegmentTemplate");
                    XElement? segmentList = Child(representation, "SegmentList") ??
                        Child(adaptation, "SegmentList") ??
                        Child(period, "SegmentList");

                    (MediaSegment? initialization, IReadOnlyList<MediaSegment> segments) = segmentTemplate is not null
                        ? ExpandTemplate(representationBase, segmentTemplate, id, ParseLong(Attribute(representation, "bandwidth")), periodDuration)
                        : segmentList is not null
                            ? ParseSegmentList(representationBase, segmentList)
                            : (null, Array.Empty<MediaSegment>());

                    representations.Add(new DashRepresentation
                    {
                        Id = id,
                        BaseUri = representationBase,
                        TrackKind = trackKind,
                        MimeType = mime,
                        Codecs = codecs,
                        Language = language,
                        Width = ParseInt(Attribute(representation, "width") ?? Attribute(adaptation, "width")),
                        Height = ParseInt(Attribute(representation, "height") ?? Attribute(adaptation, "height")),
                        FrameRate = ParseFrameRate(Attribute(representation, "frameRate") ?? Attribute(adaptation, "frameRate")),
                        Bandwidth = ParseLong(Attribute(representation, "bandwidth")),
                        AudioSamplingRate = ParseInt(Attribute(representation, "audioSamplingRate") ?? Attribute(adaptation, "audioSamplingRate")),
                        Protection = representationProtection,
                        InitializationSegment = initialization,
                        Segments = segments,
                    });
                }
            }
        }

        MediaProtection overallProtection = representations
            .Select(item => item.Protection)
            .Append(rootProtection)
            .Max();

        return new DashManifest
        {
            SourceUri = manifestUri,
            IsDynamic = isDynamic,
            Duration = totalDuration,
            MinimumUpdatePeriod = ParseDuration(Attribute(root, "minimumUpdatePeriod")),
            AvailabilityStartTime = ParseDateTime(Attribute(root, "availabilityStartTime")),
            Protection = overallProtection,
            ProtectionReason = overallProtection == MediaProtection.Drm
                ? protectionReason ?? "DASH manifest declares protected content."
                : null,
            Representations = representations,
        };
    }

    private static (MediaSegment? Initialization, IReadOnlyList<MediaSegment> Segments) ExpandTemplate(
        Uri baseUri,
        XElement template,
        string representationId,
        long? bandwidth,
        TimeSpan? periodDuration)
    {
        string? mediaPattern = Attribute(template, "media");
        string? initializationPattern = Attribute(template, "initialization");
        long timescale = ParseLong(Attribute(template, "timescale")) ?? 1;
        long startNumber = ParseLong(Attribute(template, "startNumber")) ?? 1;
        long? fixedDuration = ParseLong(Attribute(template, "duration"));

        if (timescale <= 0)
        {
            throw new FormatException("DASH SegmentTemplate timescale must be positive.");
        }

        MediaSegment? initialization = null;
        if (!string.IsNullOrWhiteSpace(initializationPattern))
        {
            string initPath = ExpandPattern(initializationPattern, representationId, bandwidth, startNumber, 0);
            initialization = new MediaSegment
            {
                SourceUri = Resolve(baseUri, initPath),
                Sequence = -1,
            };
        }

        if (string.IsNullOrWhiteSpace(mediaPattern))
        {
            return (initialization, Array.Empty<MediaSegment>());
        }

        var timelineItems = new List<(long Time, long Duration)>();
        XElement? timeline = Child(template, "SegmentTimeline");
        if (timeline is not null)
        {
            ExpandTimeline(timeline, periodDuration, timescale, timelineItems);
        }
        else if (fixedDuration is > 0 && periodDuration is not null)
        {
            double countDouble = Math.Ceiling(periodDuration.Value.TotalSeconds * timescale / fixedDuration.Value);
            int count = checked((int)Math.Min(countDouble, MaximumExpandedSegments));
            for (int index = 0; index < count; index++)
            {
                timelineItems.Add((checked(index * fixedDuration.Value), fixedDuration.Value));
            }
        }

        var segments = new List<MediaSegment>(timelineItems.Count);
        for (int index = 0; index < timelineItems.Count; index++)
        {
            long number = checked(startNumber + index);
            (long time, long duration) = timelineItems[index];
            string path = ExpandPattern(mediaPattern, representationId, bandwidth, number, time);
            segments.Add(new MediaSegment
            {
                SourceUri = Resolve(baseUri, path),
                Sequence = number,
                Duration = TimeSpan.FromSeconds((double)duration / timescale),
            });
        }

        return (initialization, segments);
    }

    private static void ExpandTimeline(
        XElement timeline,
        TimeSpan? periodDuration,
        long timescale,
        List<(long Time, long Duration)> output)
    {
        List<XElement> entries = Children(timeline, "S").ToList();
        long currentTime = 0;

        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            XElement entry = entries[entryIndex];
            long duration = ParseLong(Attribute(entry, "d")) ??
                throw new FormatException("DASH SegmentTimeline item is missing duration.");
            if (duration <= 0)
            {
                throw new FormatException("DASH SegmentTimeline duration must be positive.");
            }

            currentTime = ParseLong(Attribute(entry, "t")) ?? currentTime;
            long repeat = ParseLong(Attribute(entry, "r")) ?? 0;
            if (repeat < 0)
            {
                long? nextTime = entryIndex + 1 < entries.Count
                    ? ParseLong(Attribute(entries[entryIndex + 1], "t"))
                    : periodDuration is null
                        ? null
                        : checked((long)Math.Ceiling(periodDuration.Value.TotalSeconds * timescale));
                repeat = nextTime is null
                    ? 0
                    : Math.Max(0, checked((nextTime.Value - currentTime + duration - 1) / duration - 1));
            }

            for (long repeatIndex = 0; repeatIndex <= repeat; repeatIndex++)
            {
                if (output.Count >= MaximumExpandedSegments)
                {
                    throw new FormatException("DASH timeline expands beyond the supported segment limit.");
                }

                output.Add((currentTime, duration));
                currentTime = checked(currentTime + duration);
            }
        }
    }

    private static (MediaSegment? Initialization, IReadOnlyList<MediaSegment> Segments) ParseSegmentList(
        Uri baseUri,
        XElement segmentList)
    {
        long timescale = ParseLong(Attribute(segmentList, "timescale")) ?? 1;
        long duration = ParseLong(Attribute(segmentList, "duration")) ?? 0;
        long startNumber = ParseLong(Attribute(segmentList, "startNumber")) ?? 1;
        MediaSegment? initialization = null;

        XElement? initializationElement = Child(segmentList, "Initialization");
        if (initializationElement is not null && Attribute(initializationElement, "sourceURL") is { Length: > 0 } source)
        {
            ParseRange(Attribute(initializationElement, "range"), out long? start, out long? length);
            initialization = new MediaSegment
            {
                SourceUri = Resolve(baseUri, source),
                Sequence = -1,
                ByteRangeStart = start,
                ByteRangeLength = length,
            };
        }

        var segments = new List<MediaSegment>();
        foreach (XElement segmentElement in Children(segmentList, "SegmentURL"))
        {
            string? media = Attribute(segmentElement, "media");
            if (string.IsNullOrWhiteSpace(media))
            {
                continue;
            }

            ParseRange(Attribute(segmentElement, "mediaRange"), out long? start, out long? length);
            long sequence = checked(startNumber + segments.Count);
            segments.Add(new MediaSegment
            {
                SourceUri = Resolve(baseUri, media),
                Sequence = sequence,
                Duration = duration > 0 && timescale > 0 ? TimeSpan.FromSeconds((double)duration / timescale) : null,
                ByteRangeStart = start,
                ByteRangeLength = length,
            });
        }

        return (initialization, segments);
    }

    private static string ExpandPattern(string pattern, string representationId, long? bandwidth, long number, long time)
    {
        const string escapedDollar = "\u0000";
        string result = pattern.Replace("$$", escapedDollar, StringComparison.Ordinal)
            .Replace("$RepresentationID$", representationId, StringComparison.Ordinal)
            .Replace("$Bandwidth$", (bandwidth ?? 0).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$Time$", time.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        result = ReplaceNumberToken(result, number);
        return result.Replace(escapedDollar, "$", StringComparison.Ordinal);
    }

    private static string ReplaceNumberToken(string value, long number)
    {
        int start = value.IndexOf("$Number", StringComparison.Ordinal);
        while (start >= 0)
        {
            int end = value.IndexOf('$', start + 1);
            if (end < 0)
            {
                break;
            }

            string token = value[start..(end + 1)];
            string replacement = number.ToString(CultureInfo.InvariantCulture);
            int percent = token.IndexOf('%');
            if (percent >= 0)
            {
                string format = token[(percent + 1)..^1];
                if (format.StartsWith('0') && format.EndsWith('d') &&
                    int.TryParse(format[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int width) &&
                    width is > 0 and <= 32)
                {
                    replacement = number.ToString($"D{width}", CultureInfo.InvariantCulture);
                }
            }

            value = string.Concat(value.AsSpan(0, start), replacement, value.AsSpan(end + 1));
            start = value.IndexOf("$Number", start + replacement.Length, StringComparison.Ordinal);
        }

        return value;
    }

    private static MediaProtection DetectProtection(XElement container, out string? reason)
    {
        reason = null;
        foreach (XElement protection in Children(container, "ContentProtection"))
        {
            string? scheme = Attribute(protection, "schemeIdUri");
            string? value = Attribute(protection, "value");
            if (!string.IsNullOrWhiteSpace(scheme) || !string.IsNullOrWhiteSpace(value))
            {
                reason = $"DASH ContentProtection: {scheme ?? value}.";
                return MediaProtection.Drm;
            }
        }

        return MediaProtection.None;
    }

    private static MediaProtection MaxProtection(MediaProtection left, MediaProtection right)
    {
        return (MediaProtection)Math.Max((int)left, (int)right);
    }

    private static MediaTrackKind DetermineTrackKind(string? contentType, string? mime)
    {
        string value = contentType ?? mime ?? string.Empty;
        if (value.StartsWith("video", StringComparison.OrdinalIgnoreCase))
        {
            return MediaTrackKind.Video;
        }

        if (value.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
        {
            return MediaTrackKind.Audio;
        }

        if (value.StartsWith("text", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("subtitle", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("ttml", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("vtt", StringComparison.OrdinalIgnoreCase))
        {
            return MediaTrackKind.Subtitle;
        }

        return MediaTrackKind.Unknown;
    }

    private static IEnumerable<XElement> Children(XContainer container, string localName)
    {
        return container.Elements().Where(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal));
    }

    private static XElement? Child(XContainer container, string localName)
    {
        return Children(container, localName).FirstOrDefault();
    }

    private static string? ChildValue(XContainer container, string localName)
    {
        string? value = Child(container, localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Attribute(XElement element, string localName)
    {
        return element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value;
    }

    private static Uri ResolveBase(Uri baseUri, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? baseUri : Resolve(baseUri, value);
    }

    private static Uri Resolve(Uri baseUri, string value)
    {
        if (!Uri.TryCreate(baseUri, value, out Uri? result) ||
            (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException("DASH manifest contains an invalid or unsupported URI.");
        }

        return result;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split('/', 2);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return MediaText.ParseDouble(value);
    }

    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static void ParseRange(string? value, out long? start, out long? length)
    {
        start = null;
        length = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value.Split('-', 2);
        if (parts.Length == 2 && ParseLong(parts[0]) is long parsedStart && ParseLong(parts[1]) is long parsedEnd && parsedEnd >= parsedStart)
        {
            start = parsedStart;
            length = checked(parsedEnd - parsedStart + 1);
        }
    }
}
