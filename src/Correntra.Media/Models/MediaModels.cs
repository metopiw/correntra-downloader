using System.Collections.Immutable;

namespace Correntra.Media.Models;

public enum MediaSourceKind
{
    Direct,
    Hls,
    Dash,
    BrowserObserved,
}

public enum MediaTrackKind
{
    Unknown,
    Muxed,
    Video,
    Audio,
    Subtitle,
}

public enum MediaProtection
{
    None,
    ClearAes128,
    Drm,
}

public sealed record MediaCandidate
{
    public required string Id { get; init; }

    public required Uri SourceUri { get; init; }

    public Uri? PageUri { get; init; }

    public string? Title { get; init; }

    public string? MimeType { get; init; }

    public string? Referrer { get; init; }

    public string? Site { get; init; }

    public string? TabKey { get; init; }

    public long? ContentLength { get; init; }

    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

public sealed record MediaDescriptor
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required MediaSourceKind SourceKind { get; init; }

    public required Uri SourceUri { get; init; }

    public bool IsLive { get; init; }

    public MediaProtection Protection { get; init; }

    public string? ProtectionReason { get; init; }

    public TimeSpan? Duration { get; init; }

    public IReadOnlyList<MediaVariant> Variants { get; init; } = [];

    public IReadOnlyList<MediaSubtitle> Subtitles { get; init; } = [];

    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

public sealed record MediaVariant
{
    public required string Id { get; init; }

    public required Uri SourceUri { get; init; }

    public required MediaTrackKind TrackKind { get; init; }

    public string? DisplayName { get; init; }

    public string? MimeType { get; init; }

    public string? Container { get; init; }

    public string? Codecs { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public long? Bitrate { get; init; }

    public long? ApproximateBytes { get; init; }

    public string? Language { get; init; }

    public string? AudioGroupId { get; init; }

    public bool IsDefault { get; init; }

    public bool IsLive { get; init; }

    public MediaProtection Protection { get; init; }

    public IReadOnlyList<MediaSegment> Segments { get; init; } = [];

    public MediaSegment? InitializationSegment { get; init; }
}

public sealed record MediaSubtitle(
    string Id,
    Uri SourceUri,
    string? Language,
    string? Name,
    string? Format,
    bool IsDefault);

public sealed record MediaSegment
{
    public required Uri SourceUri { get; init; }

    public long Sequence { get; init; }

    public TimeSpan? Duration { get; init; }

    public long? ByteRangeStart { get; init; }

    public long? ByteRangeLength { get; init; }

    public bool Discontinuity { get; init; }

    public MediaEncryption? Encryption { get; init; }
}

public sealed record MediaEncryption(
    string Method,
    Uri? KeyUri,
    byte[]? InitializationVector,
    string? KeyFormat);
