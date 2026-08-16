using Correntra.Tools;

namespace Correntra.Tools.Tests;

public sealed class CrawlFrontierTests
{
    [Fact]
    public void Constructor_schedules_canonical_seed_at_depth_zero()
    {
        var frontier = new CrawlFrontier(new Uri("HTTPS://EXAMPLE.COM:443/root#fragment"));

        Assert.Equal(1, frontier.PendingCount);
        Assert.Equal(1, frontier.ScheduledCount);
        Assert.True(frontier.TryDequeue(out var request));
        Assert.Equal("https://example.com/root", request.Url.AbsoluteUri);
        Assert.Equal(0, request.Depth);
        Assert.Null(request.Referrer);
        Assert.False(frontier.TryDequeue(out _));
    }

    [Fact]
    public void Schedule_uses_fifo_order_depth_and_referrer()
    {
        var frontier = new CrawlFrontier(new Uri("https://example.com/root"));
        Assert.True(frontier.TryDequeue(out var seed));

        var first = frontier.TrySchedule(new Uri("https://example.com/a"), seed);
        var second = frontier.TrySchedule(new Uri("https://example.com/b"), seed);

        Assert.True(first.WasAdded);
        Assert.True(second.WasAdded);
        Assert.Equal(1, first.Request!.Depth);
        Assert.Equal(seed.Url, first.Request.Referrer);
        Assert.True(frontier.TryDequeue(out var dequeuedFirst));
        Assert.True(frontier.TryDequeue(out var dequeuedSecond));
        Assert.Equal("/a", dequeuedFirst.Url.AbsolutePath);
        Assert.Equal("/b", dequeuedSecond.Url.AbsolutePath);
    }

    [Fact]
    public void Schedule_deduplicates_fragments_and_default_ports()
    {
        var frontier = new CrawlFrontier(new Uri("https://example.com/root"));
        Assert.True(frontier.TryDequeue(out var seed));

        Assert.True(frontier.TrySchedule(new Uri("https://example.com:443/a#one"), seed).WasAdded);
        var duplicate = frontier.TrySchedule(new Uri("https://EXAMPLE.COM/a#two"), seed);

        Assert.Equal(CrawlScheduleStatus.Duplicate, duplicate.Status);
        Assert.Equal(2, frontier.ScheduledCount);
    }

    [Theory]
    [InlineData("http://example.com/a")]
    [InlineData("https://other.example.com/a")]
    [InlineData("https://example.com:444/a")]
    public void Same_origin_policy_rejects_scheme_host_or_port_changes(string value)
    {
        var frontier = new CrawlFrontier(new Uri("https://example.com/root"));
        Assert.True(frontier.TryDequeue(out var seed));

        var result = frontier.TrySchedule(new Uri(value), seed);

        Assert.Equal(CrawlScheduleStatus.DifferentOrigin, result.Status);
    }

    [Fact]
    public void Cross_origin_pages_can_be_enabled_explicitly()
    {
        var frontier = new CrawlFrontier(
            new Uri("https://example.com/root"),
            new CrawlFrontierOptions { SameOriginOnly = false });
        Assert.True(frontier.TryDequeue(out var seed));

        var result = frontier.TrySchedule(new Uri("https://cdn.example.net/a"), seed);

        Assert.True(result.WasAdded);
    }

    [Fact]
    public void Maximum_depth_is_enforced_before_queueing()
    {
        var frontier = new CrawlFrontier(
            new Uri("https://example.com/root"),
            new CrawlFrontierOptions { MaximumDepth = 1 });
        Assert.True(frontier.TryDequeue(out var seed));
        var levelOne = frontier.TrySchedule(new Uri("https://example.com/one"), seed).Request!;

        var result = frontier.TrySchedule(new Uri("https://example.com/two"), levelOne);

        Assert.Equal(CrawlScheduleStatus.DepthExceeded, result.Status);
        Assert.Equal(2, frontier.ScheduledCount);
    }

    [Fact]
    public void Maximum_pages_counts_seed_and_processed_requests()
    {
        var frontier = new CrawlFrontier(
            new Uri("https://example.com/root"),
            new CrawlFrontierOptions { MaximumPages = 2 });
        Assert.True(frontier.TryDequeue(out var seed));
        Assert.True(frontier.TrySchedule(new Uri("https://example.com/one"), seed).WasAdded);
        Assert.True(frontier.TryDequeue(out _));

        var result = frontier.TrySchedule(new Uri("https://example.com/two"), seed);

        Assert.Equal(CrawlScheduleStatus.PageLimitReached, result.Status);
        Assert.Equal(2, frontier.ScheduledCount);
    }

    [Theory]
    [InlineData("ftp://example.com/a", UrlRejectionReason.UnsupportedScheme)]
    [InlineData("https://user:secret@example.com/a", UrlRejectionReason.EmbeddedCredentials)]
    [InlineData("http://127.0.0.1/a", UrlRejectionReason.NonPublicAddress)]
    public void Unsafe_page_is_rejected_with_specific_reason(string value, UrlRejectionReason expected)
    {
        var frontier = new CrawlFrontier(
            new Uri("https://example.com/root"),
            new CrawlFrontierOptions { SameOriginOnly = false });
        Assert.True(frontier.TryDequeue(out var seed));

        var result = frontier.TrySchedule(new Uri(value), seed);

        Assert.Equal(CrawlScheduleStatus.UnsafeUrl, result.Status);
        Assert.Equal(expected, result.UrlSafetyReason);
    }

    [Fact]
    public void Private_seed_requires_an_explicit_policy_exception()
    {
        Assert.Throws<ArgumentException>(() => new CrawlFrontier(new Uri("http://192.168.1.3/root")));

        var frontier = new CrawlFrontier(
            new Uri("http://192.168.1.3/root"),
            new CrawlFrontierOptions
            {
                SafetyPolicy = new UrlSafetyPolicy { AllowNonPublicAddresses = true },
            });

        Assert.Equal(1, frontier.ScheduledCount);
    }

    [Fact]
    public void Concurrent_duplicate_scheduling_remains_unique()
    {
        var frontier = new CrawlFrontier(new Uri("https://example.com/root"));
        Assert.True(frontier.TryDequeue(out var seed));

        Parallel.For(0, 100, _ => frontier.TrySchedule(new Uri("https://example.com/same"), seed));

        Assert.Equal(2, frontier.ScheduledCount);
        Assert.Equal(1, frontier.PendingCount);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(101, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1_000_001)]
    public void Constructor_rejects_invalid_limits(int maximumDepth, int maximumPages)
    {
        var options = new CrawlFrontierOptions
        {
            MaximumDepth = maximumDepth,
            MaximumPages = maximumPages,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CrawlFrontier(new Uri("https://example.com"), options));
    }
}
