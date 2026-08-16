using Correntra.Tools;

namespace Correntra.Tools.Tests;

public sealed class UrlListParserTests
{
    [Fact]
    public void Empty_input_returns_empty_result()
    {
        var result = UrlListParser.Parse(null);

        Assert.Empty(result.Urls);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_ignores_comments_and_trims_matching_quotes()
    {
        const string Input = """
            # exported list
            "https://example.com/one.zip"
            ; disabled URL
            'https://example.com/two.zip'
            """;

        var result = UrlListParser.Parse(Input);

        Assert.Equal(
            ["https://example.com/one.zip", "https://example.com/two.zip"],
            result.Urls.Select(static url => url.AbsoluteUri));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_canonicalizes_and_deduplicates_first_occurrence()
    {
        const string Input = """
            HTTPS://EXAMPLE.COM:443/a.zip#one
            https://example.com/a.zip#two
            https://example.com/B.zip
            https://example.com/b.zip
            """;

        var result = UrlListParser.Parse(Input);

        Assert.Equal(3, result.Urls.Count);
        Assert.Equal("https://example.com/a.zip", result.Urls[0].AbsoluteUri);
        Assert.Equal("https://example.com/B.zip", result.Urls[1].AbsoluteUri);
        Assert.Equal("https://example.com/b.zip", result.Urls[2].AbsoluteUri);
    }

    [Fact]
    public void Parse_reports_invalid_and_unsafe_lines_without_raw_values()
    {
        const string Input = """
            not a URL with secret=alpha
            ftp://example.com/file
            https://user:secret@example.com/file
            http://127.0.0.1/admin
            """;

        var result = UrlListParser.Parse(Input);

        Assert.Empty(result.Urls);
        Assert.Collection(
            result.Issues,
            issue => Assert.Equal(UrlListIssueKind.InvalidUrl, issue.Kind),
            issue => Assert.Equal(UrlRejectionReason.UnsupportedScheme, issue.SafetyReason),
            issue => Assert.Equal(UrlRejectionReason.EmbeddedCredentials, issue.SafetyReason),
            issue => Assert.Equal(UrlRejectionReason.NonPublicAddress, issue.SafetyReason));
    }

    [Fact]
    public void Parse_stops_at_unique_url_limit()
    {
        const string Input = """
            https://example.com/1
            https://example.com/1#duplicate
            https://example.com/2
            https://example.com/3
            https://example.com/4
            """;
        var options = new UrlListParseOptions { MaximumUrls = 2 };

        var result = UrlListParser.Parse(Input, options);

        Assert.Equal(2, result.Urls.Count);
        Assert.Single(result.Issues);
        Assert.Equal(UrlListIssueKind.LimitReached, result.Issues[0].Kind);
        Assert.Equal(4, result.Issues[0].LineNumber);
    }

    [Fact]
    public void Parse_honors_explicit_private_network_policy()
    {
        var result = UrlListParser.Parse(
            "http://192.168.0.10/file",
            new UrlListParseOptions
            {
                SafetyPolicy = new UrlSafetyPolicy { AllowNonPublicAddresses = true },
            });

        Assert.Single(result.Urls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void Parse_rejects_invalid_limits(int maximumUrls)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UrlListParser.Parse("https://example.com", new UrlListParseOptions { MaximumUrls = maximumUrls }));
    }
}
