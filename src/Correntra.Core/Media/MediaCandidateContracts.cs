using System.Collections.Immutable;
using Correntra.Core.Downloads;
using Correntra.Core.Internal;

namespace Correntra.Core.Media;

public enum MediaKind
{
    Video = 0,
    Audio = 1,
}

public enum MediaTransport
{
    Direct = 0,
    Hls = 1,
    Dash = 2,
}

public enum MediaProtection
{
    Clear = 0,
    Suspected = 1,
    DrmProtected = 2,
}

public enum MediaTrackKind
{
    Video = 0,
    Audio = 1,
    Subtitle = 2,
}

public readonly record struct MediaCandidateId
{
    public MediaCandidateId(string value)
    {
        Value = OpaqueToken.Validate(value, nameof(value));
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct MediaTrackId
{
    public MediaTrackId(string value)
    {
        Value = OpaqueToken.Validate(value, nameof(value), 1, 128);
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? string.Empty;
}

public sealed record MediaTrack
{
    public MediaTrack(
        MediaTrackId id,
        MediaTrackKind kind,
        string label,
        string? language = null,
        string? codec = null,
        string? container = null,
        int? width = null,
        int? height = null,
        long? bitrate = null,
        long? approximateBytes = null,
        bool isDefault = false)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A media track ID is required.", nameof(id));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if ((width is null) != (height is null) || width is <= 0 || height is <= 0)
        {
            throw new ArgumentException("Video dimensions must be positive and supplied together.", nameof(width));
        }

        if (kind != MediaTrackKind.Video && (width is not null || height is not null))
        {
            throw new ArgumentException("Only video tracks can have dimensions.", nameof(width));
        }

        if (bitrate is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrate));
        }

        if (approximateBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approximateBytes));
        }

        Id = id;
        Kind = kind;
        Label = Guard.NotNullOrWhiteSpace(label, nameof(label), 200);
        Language = NormalizeOptional(language, nameof(language), 35);
        Codec = NormalizeOptional(codec, nameof(codec), 100);
        Container = NormalizeOptional(container, nameof(container), 32);
        Width = width;
        Height = height;
        Bitrate = bitrate;
        ApproximateBytes = approximateBytes;
        IsDefault = isDefault;
    }

    public MediaTrackId Id { get; }

    public MediaTrackKind Kind { get; }

    public string Label { get; }

    public string? Language { get; }

    public string? Codec { get; }

    public string? Container { get; }

    public int? Width { get; }

    public int? Height { get; }

    public long? Bitrate { get; }

    public long? ApproximateBytes { get; }

    public bool IsDefault { get; }

    private static string? NormalizeOptional(string? value, string parameterName, int maximumLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Guard.NotNullOrWhiteSpace(value, parameterName, maximumLength);
    }
}

public sealed class CapturedMediaCandidate
{
    public CapturedMediaCandidate(
        MediaCandidateId id,
        MediaKind kind,
        MediaTransport transport,
        string title,
        Uri pageUrl,
        DownloadSource source,
        DateTimeOffset detectedAtUtc,
        IEnumerable<MediaTrack> tracks,
        bool isLive = false,
        MediaProtection protection = MediaProtection.Clear,
        string? protectionReason = null,
        TimeSpan? duration = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A media candidate ID is required.", nameof(id));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(transport))
        {
            throw new ArgumentOutOfRangeException(nameof(transport));
        }

        if (!Enum.IsDefined(protection))
        {
            throw new ArgumentOutOfRangeException(nameof(protection));
        }

        if (duration.HasValue && duration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        ImmutableArray<MediaTrack> materializedTracks = tracks?.ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(tracks));
        if (materializedTracks.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A media candidate requires at least one track.", nameof(tracks));
        }

        if (materializedTracks.Any(static track => track is null) ||
            materializedTracks.GroupBy(static track => track.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Media tracks cannot be null or have duplicate IDs.", nameof(tracks));
        }

        if (protection == MediaProtection.DrmProtected && string.IsNullOrWhiteSpace(protectionReason))
        {
            throw new ArgumentException("DRM-protected media requires a classification reason.", nameof(protectionReason));
        }

        Id = id;
        Kind = kind;
        Transport = transport;
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), 500);
        PageUrl = Guard.HttpUri(pageUrl, nameof(pageUrl));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        DetectedAtUtc = Guard.UtcTimestamp(detectedAtUtc, nameof(detectedAtUtc));
        Tracks = materializedTracks;
        IsLive = isLive;
        Protection = protection;
        ProtectionReason = string.IsNullOrWhiteSpace(protectionReason)
            ? null
            : Guard.NotNullOrWhiteSpace(protectionReason, nameof(protectionReason), 500);
        Duration = duration;
    }

    public MediaCandidateId Id { get; }

    public MediaKind Kind { get; }

    public MediaTransport Transport { get; }

    public string Title { get; }

    public Uri PageUrl { get; }

    public DownloadSource Source { get; }

    public DateTimeOffset DetectedAtUtc { get; }

    public ImmutableArray<MediaTrack> Tracks { get; }

    public bool IsLive { get; }

    public MediaProtection Protection { get; }

    public string? ProtectionReason { get; }

    public TimeSpan? Duration { get; }

    public bool CanCreateDownload => Protection == MediaProtection.Clear;

    public MediaCandidateSummary ToSummary()
    {
        return new MediaCandidateSummary(Id, Kind, Title, IsLive, Protection, Tracks, Duration);
    }
}

public sealed record MediaCandidateSummary
{
    public MediaCandidateSummary(
        MediaCandidateId id,
        MediaKind kind,
        string title,
        bool isLive,
        MediaProtection protection,
        IEnumerable<MediaTrack> tracks,
        TimeSpan? duration = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A media candidate ID is required.", nameof(id));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(protection))
        {
            throw new ArgumentOutOfRangeException(nameof(protection));
        }

        Id = id;
        Kind = kind;
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), 500);
        IsLive = isLive;
        Protection = protection;
        Tracks = tracks?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(tracks));
        Duration = duration;
    }

    public MediaCandidateId Id { get; }

    public MediaKind Kind { get; }

    public string Title { get; }

    public bool IsLive { get; }

    public MediaProtection Protection { get; }

    public ImmutableArray<MediaTrack> Tracks { get; }

    public TimeSpan? Duration { get; }
}

public sealed record MediaSelectionRequest
{
    public MediaSelectionRequest(
        MediaCandidateId candidateId,
        MediaTrackId primaryTrackId,
        MediaTrackId? audioTrackId = null,
        IEnumerable<MediaTrackId>? subtitleTrackIds = null,
        bool extractAudioOnly = false,
        string? requestedContainer = null)
    {
        if (candidateId.IsEmpty)
        {
            throw new ArgumentException("A media candidate ID is required.", nameof(candidateId));
        }

        if (primaryTrackId.IsEmpty)
        {
            throw new ArgumentException("A primary track ID is required.", nameof(primaryTrackId));
        }

        if (audioTrackId is { IsEmpty: true })
        {
            throw new ArgumentException("An audio track ID cannot be empty.", nameof(audioTrackId));
        }

        ImmutableArray<MediaTrackId> subtitles = (subtitleTrackIds ?? []).ToImmutableArray();
        if (subtitles.Any(static id => id.IsEmpty) || subtitles.Distinct().Count() != subtitles.Length)
        {
            throw new ArgumentException("Subtitle track IDs must be non-empty and unique.", nameof(subtitleTrackIds));
        }

        CandidateId = candidateId;
        PrimaryTrackId = primaryTrackId;
        AudioTrackId = audioTrackId;
        SubtitleTrackIds = subtitles;
        ExtractAudioOnly = extractAudioOnly;
        RequestedContainer = string.IsNullOrWhiteSpace(requestedContainer)
            ? null
            : Guard.NotNullOrWhiteSpace(requestedContainer, nameof(requestedContainer), 32).ToLowerInvariant();
    }

    public MediaCandidateId CandidateId { get; }

    public MediaTrackId PrimaryTrackId { get; }

    public MediaTrackId? AudioTrackId { get; }

    public ImmutableArray<MediaTrackId> SubtitleTrackIds { get; }

    public bool ExtractAudioOnly { get; }

    public string? RequestedContainer { get; }

    public void ValidateAgainst(CapturedMediaCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Id != CandidateId)
        {
            throw new InvalidOperationException("The selection belongs to a different media candidate.");
        }

        if (!candidate.CanCreateDownload)
        {
            throw new InvalidOperationException("DRM-protected media cannot create a download job.");
        }

        if (!candidate.Tracks.Any(track => track.Id == PrimaryTrackId) ||
            (AudioTrackId is { } audioId && !candidate.Tracks.Any(track => track.Id == audioId && track.Kind == MediaTrackKind.Audio)) ||
            SubtitleTrackIds.Any(subtitleId => !candidate.Tracks.Any(track => track.Id == subtitleId && track.Kind == MediaTrackKind.Subtitle)))
        {
            throw new InvalidOperationException("The selection contains an unavailable or incompatible media track.");
        }
    }
}
