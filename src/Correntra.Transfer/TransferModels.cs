using System.Collections.ObjectModel;
using System.Net;

namespace Correntra.Transfer;

/// <summary>Describes a single HTTP or HTTPS transfer.</summary>
public sealed record DownloadRequest
{
    public DownloadRequest(Uri source, string destinationPath)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        DestinationPath = !string.IsNullOrWhiteSpace(destinationPath)
            ? destinationPath
            : throw new ArgumentException("A destination path is required.", nameof(destinationPath));
    }

    public Uri Source { get; }

    public string DestinationPath { get; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public int MaxSegments { get; init; } = 8;

    public long MinimumSegmentSizeBytes { get; init; } = 2 * 1024 * 1024;

    public bool Overwrite { get; init; }

    public RetryOptions Retry { get; init; } = new();

    public PauseToken PauseToken { get; init; }

    public IBandwidthLimiter? BandwidthLimiter { get; init; }

    public IProgress<TransferProgress>? Progress { get; init; }

    public HashRequirement? ExpectedHash { get; init; }
}

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 8;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    public double JitterFactor { get; init; } = 0.2;
}

public enum TransferHashAlgorithm
{
    Sha256,
    Sha384,
    Sha512,
}

public sealed record HashRequirement(TransferHashAlgorithm Algorithm, string HexDigest);

public enum TransferPhase
{
    Probing,
    Connecting,
    Downloading,
    Paused,
    Throttled,
    Verifying,
    Finalizing,
    Completed,
}

public sealed record TransferProgress(
    TransferPhase Phase,
    long BytesTransferred,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    int ActiveSegments,
    bool IsThrottled);

public sealed record DownloadResult(
    Uri FinalUri,
    string DestinationPath,
    long BytesTransferred,
    string? VerifiedHash,
    bool WasResumed,
    TimeSpan Elapsed);

public sealed record RemoteResourceInfo(
    Uri RequestedUri,
    Uri FinalUri,
    HttpStatusCode StatusCode,
    long? ContentLength,
    bool SupportsRanges,
    string? EntityTag,
    DateTimeOffset? LastModified,
    string SuggestedFileName,
    string? ContentType);

public readonly record struct ByteRange(long Start, long EndInclusive)
{
    public long Length => checked(EndInclusive - Start + 1);
}
