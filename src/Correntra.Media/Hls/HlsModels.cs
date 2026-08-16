using Correntra.Media.Models;

namespace Correntra.Media.Hls;

public sealed record HlsPlaylist
{
    public required Uri SourceUri { get; init; }

    public required bool IsMaster { get; init; }

    public bool IsLive { get; init; }

    public bool HasEndList { get; init; }

    public long MediaSequence { get; init; }

    public TimeSpan? TargetDuration { get; init; }

    public IReadOnlyList<HlsVariant> Variants { get; init; } = [];

    public IReadOnlyList<HlsRendition> Renditions { get; init; } = [];

    public IReadOnlyList<MediaSegment> Segments { get; init; } = [];

    public MediaSegment? InitializationSegment { get; init; }

    public MediaProtection Protection { get; init; }

    public string? ProtectionReason { get; init; }
}

public sealed record HlsVariant
{
    public required Uri SourceUri { get; init; }

    public long? Bandwidth { get; init; }

    public long? AverageBandwidth { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public string? Codecs { get; init; }

    public string? Name { get; init; }

    public string? AudioGroupId { get; init; }

    public string? SubtitleGroupId { get; init; }
}

public sealed record HlsRendition
{
    public required string Type { get; init; }

    public required string GroupId { get; init; }

    public string? Name { get; init; }

    public string? Language { get; init; }

    public Uri? SourceUri { get; init; }

    public bool IsDefault { get; init; }

    public bool AutoSelect { get; init; }

    public bool Forced { get; init; }

    public string? Channels { get; init; }
}

