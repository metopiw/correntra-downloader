using Correntra.Media.Models;

namespace Correntra.Media.Dash;

public sealed record DashManifest
{
    public required Uri SourceUri { get; init; }

    public bool IsDynamic { get; init; }

    public TimeSpan? Duration { get; init; }

    public TimeSpan? MinimumUpdatePeriod { get; init; }

    public DateTimeOffset? AvailabilityStartTime { get; init; }

    public MediaProtection Protection { get; init; }

    public string? ProtectionReason { get; init; }

    public IReadOnlyList<DashRepresentation> Representations { get; init; } = [];
}

public sealed record DashRepresentation
{
    public required string Id { get; init; }

    public required Uri BaseUri { get; init; }

    public required MediaTrackKind TrackKind { get; init; }

    public string? MimeType { get; init; }

    public string? Codecs { get; init; }

    public string? Language { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public long? Bandwidth { get; init; }

    public int? AudioSamplingRate { get; init; }

    public MediaProtection Protection { get; init; }

    public MediaSegment? InitializationSegment { get; init; }

    public IReadOnlyList<MediaSegment> Segments { get; init; } = [];
}

