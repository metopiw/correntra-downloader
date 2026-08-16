using Correntra.Tools;

namespace Correntra.Tools.Tests;

public sealed class BatchPatternExpanderTests
{
    [Fact]
    public void Inline_numeric_range_preserves_zero_padding()
    {
        var urls = BatchPatternExpander.ExpandInline("https://example.com/image_[007-010].jpg");

        Assert.Equal(
            [
                "https://example.com/image_007.jpg",
                "https://example.com/image_008.jpg",
                "https://example.com/image_009.jpg",
                "https://example.com/image_010.jpg",
            ],
            urls.Select(static url => url.AbsoluteUri));
    }

    [Fact]
    public void Inline_numeric_range_can_descend_with_a_step()
    {
        var urls = BatchPatternExpander.ExpandInline("https://example.com/part-[10-1:3].bin");

        Assert.Equal(
            ["part-10.bin", "part-7.bin", "part-4.bin", "part-1.bin"],
            urls.Select(static url => url.Segments[^1]));
    }

    [Fact]
    public void Inline_numeric_step_stays_inside_inclusive_boundary()
    {
        var urls = BatchPatternExpander.ExpandInline("https://example.com/[1-6:4]");

        Assert.Equal(["1", "5"], urls.Select(static url => url.Segments[^1]));
    }

    [Fact]
    public void Inline_alphabetic_ranges_can_ascend_and_descend()
    {
        var ascending = BatchPatternExpander.ExpandInline("https://example.com/[a-e:2].txt");
        var descending = BatchPatternExpander.ExpandInline("https://example.com/[D-A].txt");

        Assert.Equal(["a.txt", "c.txt", "e.txt"], ascending.Select(static url => url.Segments[^1]));
        Assert.Equal(["D.txt", "C.txt", "B.txt", "A.txt"], descending.Select(static url => url.Segments[^1]));
    }

    [Fact]
    public void Multiple_inline_ranges_use_left_to_right_cartesian_order()
    {
        var urls = BatchPatternExpander.ExpandInline("https://example.com/[1-2]/[a-b].zip");

        Assert.Equal(
            [
                "https://example.com/1/a-b.zip".Replace("a-b", "a", StringComparison.Ordinal),
                "https://example.com/1/b.zip",
                "https://example.com/2/a.zip",
                "https://example.com/2/b.zip",
            ],
            urls.Select(static url => url.AbsoluteUri));
    }

    [Fact]
    public void Wildcard_axes_are_expanded_in_placeholder_order()
    {
        BatchAxis[] axes = [new NumericBatchAxis(8, 9, width: 2), new AlphabeticBatchAxis('x', 'z', step: 2)];

        var urls = BatchPatternExpander.ExpandWildcards("https://example.com/ch-*/frame-*.png", axes);

        Assert.Equal(
            [
                "https://example.com/ch-08/frame-x.png",
                "https://example.com/ch-08/frame-z.png",
                "https://example.com/ch-09/frame-x.png",
                "https://example.com/ch-09/frame-z.png",
            ],
            urls.Select(static url => url.AbsoluteUri));
    }

    [Fact]
    public void Pattern_without_ranges_returns_one_canonical_url()
    {
        var urls = BatchPatternExpander.ExpandInline("HTTPS://EXAMPLE.COM:443/file.zip#fragment");

        Assert.Equal("https://example.com/file.zip", Assert.Single(urls).AbsoluteUri);
    }

    [Fact]
    public void Unrecognized_square_brackets_are_left_as_literal_url_text()
    {
        var urls = BatchPatternExpander.ExpandInline("https://example.com/api?items[]=one");

        Assert.Single(urls);
        Assert.Contains("items[]=one", urls[0].OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public void Expansion_rejects_result_count_above_limit_before_allocating()
    {
        var options = new BatchExpansionOptions { MaximumResults = 12 };

        var exception = Assert.Throws<BatchPatternException>(() =>
            BatchPatternExpander.ExpandInline("https://example.com/[1-4]/[a-d]", options));

        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expansion_rejects_unsafe_or_non_http_destinations()
    {
        Assert.Throws<BatchPatternException>(() => BatchPatternExpander.ExpandInline("ftp://example.com/[1-2]"));
        Assert.Throws<BatchPatternException>(() => BatchPatternExpander.ExpandInline("http://127.0.0.1/[1-2]"));
    }

    [Fact]
    public void Expansion_can_explicitly_target_a_trusted_private_service()
    {
        var options = new BatchExpansionOptions
        {
            SafetyPolicy = new UrlSafetyPolicy { AllowNonPublicAddresses = true },
        };

        var urls = BatchPatternExpander.ExpandInline("http://192.168.1.2/[1-2]", options);

        Assert.Equal(2, urls.Count);
    }

    [Fact]
    public void Wildcard_count_must_equal_axis_count()
    {
        Assert.Throws<BatchPatternException>(() =>
            BatchPatternExpander.ExpandWildcards(
                "https://example.com/*/*",
                [new NumericBatchAxis(1, 2)]));
    }

    [Theory]
    [InlineData(-1, 4, 0, 1)]
    [InlineData(1, -1, 0, 1)]
    [InlineData(1, 4, -1, 1)]
    [InlineData(1, 400, 2, 1)]
    [InlineData(1, 4, 0, 0)]
    public void Numeric_axis_rejects_invalid_arguments(int start, int end, int width, int step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NumericBatchAxis(start, end, width, step));
    }

    [Theory]
    [InlineData('a', 'Z', 1)]
    [InlineData('1', '9', 1)]
    [InlineData('a', 'z', 0)]
    public void Alphabetic_axis_rejects_invalid_arguments(char start, char end, int step)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AlphabeticBatchAxis(start, end, step));
    }

    [Fact]
    public void Invalid_step_in_inline_pattern_has_clear_failure()
    {
        Assert.Throws<BatchPatternException>(() =>
            BatchPatternExpander.ExpandInline("https://example.com/[1-3:0]"));
    }
}
