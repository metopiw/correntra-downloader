using System.Diagnostics.CodeAnalysis;

namespace Correntra.Tools;

/// <summary>Explains why a discovered page was not added to a crawl frontier.</summary>
public enum CrawlScheduleStatus
{
    /// <summary>The page was added.</summary>
    Added,

    /// <summary>The URL failed the configured safety policy.</summary>
    UnsafeUrl,

    /// <summary>The page has already been scheduled.</summary>
    Duplicate,

    /// <summary>The page would exceed the configured depth.</summary>
    DepthExceeded,

    /// <summary>The page belongs to a different origin.</summary>
    DifferentOrigin,

    /// <summary>The total page limit has been reached.</summary>
    PageLimitReached,
}

/// <summary>Represents a deterministic crawl request.</summary>
/// <param name="Url">The canonical page URL.</param>
/// <param name="Depth">Zero for the seed and one greater for each followed page.</param>
/// <param name="Referrer">The page that discovered this request, if any.</param>
public sealed record CrawlRequest(Uri Url, int Depth, Uri? Referrer);

/// <summary>Contains the outcome of attempting to schedule a crawl request.</summary>
/// <param name="Status">The scheduling status.</param>
/// <param name="Request">The added request, or <see langword="null"/> when rejected.</param>
/// <param name="UrlSafetyReason">The specific URL rejection, when applicable.</param>
public readonly record struct CrawlScheduleResult(
    CrawlScheduleStatus Status,
    CrawlRequest? Request = null,
    UrlRejectionReason UrlSafetyReason = UrlRejectionReason.None)
{
    /// <summary>Gets whether the URL was added.</summary>
    public bool WasAdded => Status == CrawlScheduleStatus.Added;
}

/// <summary>Controls same-origin crawling, depth, page count, and URL safety.</summary>
public sealed record CrawlFrontierOptions
{
    /// <summary>Gets the default frontier options.</summary>
    public static CrawlFrontierOptions Default { get; } = new();

    /// <summary>Gets or initializes the greatest accepted depth.</summary>
    public int MaximumDepth { get; init; } = 2;

    /// <summary>Gets or initializes the total number of unique pages, including the seed.</summary>
    public int MaximumPages { get; init; } = 500;

    /// <summary>Gets or initializes whether page URLs must share scheme, host, and port with the seed.</summary>
    public bool SameOriginOnly { get; init; } = true;

    /// <summary>Gets or initializes URL safety exceptions. Internet-only URLs are accepted by default.</summary>
    public UrlSafetyPolicy SafetyPolicy { get; init; } = UrlSafetyPolicy.Strict;
}

/// <summary>
/// Maintains a thread-safe FIFO crawl frontier with stable deduplication. The frontier performs lexical URL
/// checks; call <see cref="UrlGuard.EvaluateForRequestAsync"/> immediately before each network request too.
/// </summary>
public sealed class CrawlFrontier
{
    private readonly object syncRoot = new();
    private readonly Queue<CrawlRequest> pending = new();
    private readonly HashSet<string> scheduled = new(StringComparer.Ordinal);
    private readonly CrawlFrontierOptions options;
    private readonly Uri seedOrigin;

    /// <summary>Initializes a frontier and schedules its seed at depth zero.</summary>
    /// <param name="seedUrl">The absolute seed page.</param>
    /// <param name="options">Optional same-origin, depth, page count, and safety policy.</param>
    public CrawlFrontier(Uri seedUrl, CrawlFrontierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(seedUrl);
        this.options = options ?? CrawlFrontierOptions.Default;
        ValidateOptions(this.options);

        var safety = UrlGuard.Evaluate(seedUrl, this.options.SafetyPolicy);
        if (!safety.IsAllowed)
        {
            throw new ArgumentException($"The seed URL was rejected: {safety.Reason}.", nameof(seedUrl));
        }

        var canonical = UrlGuard.Canonicalize(seedUrl);
        seedOrigin = canonical;
        var request = new CrawlRequest(canonical, 0, null);
        pending.Enqueue(request);
        scheduled.Add(canonical.AbsoluteUri);
    }

    /// <summary>Gets the number of requests waiting to be processed.</summary>
    public int PendingCount
    {
        get
        {
            lock (syncRoot)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>Gets the number of unique requests ever scheduled, including processed requests.</summary>
    public int ScheduledCount
    {
        get
        {
            lock (syncRoot)
            {
                return scheduled.Count;
            }
        }
    }

    /// <summary>Attempts to remove the oldest pending request.</summary>
    /// <param name="request">The request, when one was available.</param>
    /// <returns><see langword="true"/> when a request was returned.</returns>
    public bool TryDequeue([NotNullWhen(true)] out CrawlRequest? request)
    {
        lock (syncRoot)
        {
            return pending.TryDequeue(out request);
        }
    }

    /// <summary>Attempts to schedule an absolute child URL one level below its referrer.</summary>
    /// <param name="candidateUrl">The discovered absolute page URL.</param>
    /// <param name="referrer">The request that contained the link.</param>
    /// <returns>A detailed scheduling outcome.</returns>
    public CrawlScheduleResult TrySchedule(Uri candidateUrl, CrawlRequest referrer)
    {
        ArgumentNullException.ThrowIfNull(candidateUrl);
        ArgumentNullException.ThrowIfNull(referrer);

        var safety = UrlGuard.Evaluate(candidateUrl, options.SafetyPolicy);
        if (!safety.IsAllowed)
        {
            return new CrawlScheduleResult(
                CrawlScheduleStatus.UnsafeUrl,
                UrlSafetyReason: safety.Reason);
        }

        var depth = checked(referrer.Depth + 1);
        if (depth > options.MaximumDepth)
        {
            return new CrawlScheduleResult(CrawlScheduleStatus.DepthExceeded);
        }

        var canonical = UrlGuard.Canonicalize(candidateUrl);
        if (options.SameOriginOnly && !HasSameOrigin(seedOrigin, canonical))
        {
            return new CrawlScheduleResult(CrawlScheduleStatus.DifferentOrigin);
        }

        lock (syncRoot)
        {
            if (scheduled.Contains(canonical.AbsoluteUri))
            {
                return new CrawlScheduleResult(CrawlScheduleStatus.Duplicate);
            }

            if (scheduled.Count >= options.MaximumPages)
            {
                return new CrawlScheduleResult(CrawlScheduleStatus.PageLimitReached);
            }

            var request = new CrawlRequest(canonical, depth, referrer.Url);
            scheduled.Add(canonical.AbsoluteUri);
            pending.Enqueue(request);
            return new CrawlScheduleResult(CrawlScheduleStatus.Added, request);
        }
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static void ValidateOptions(CrawlFrontierOptions options)
    {
        if (options.MaximumDepth is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumDepth must be between 0 and 100.");
        }

        if (options.MaximumPages is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumPages must be between 1 and 1,000,000.");
        }
    }
}
