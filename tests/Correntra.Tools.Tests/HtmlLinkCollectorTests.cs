using Correntra.Tools;

namespace Correntra.Tools.Tests;

public sealed class HtmlLinkCollectorTests
{
    private static readonly Uri DocumentUrl = new("https://example.com/docs/page.html");

    [Fact]
    public void Collect_resolves_relative_and_protocol_relative_urls()
    {
        const string Html = """
            <a href="../files/a.zip">A</a>
            <img src="//cdn.example.com/images/p.png">
            <a href="/root/b.zip">B</a>
            """;

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal(
            [
                "https://example.com/files/a.zip",
                "https://cdn.example.com/images/p.png",
                "https://example.com/root/b.zip",
            ],
            result.Links.Select(static link => link.Url.AbsoluteUri));
    }

    [Fact]
    public void Collect_uses_first_safe_base_element_for_all_relative_links()
    {
        const string Html = """
            <a href="before.zip">Before</a>
            <base href="https://assets.example.net/releases/">
            <base href="https://ignored.example.org/">
            <a href="after.zip">After</a>
            """;

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal("https://assets.example.net/releases/", result.EffectiveBaseUrl.AbsoluteUri);
        Assert.Equal(
            [
                "https://assets.example.net/releases/before.zip",
                "https://assets.example.net/releases/",
                "https://ignored.example.org/",
                "https://assets.example.net/releases/after.zip",
            ],
            result.Links.Select(static link => link.Url.AbsoluteUri));
    }

    [Fact]
    public void Collect_decodes_entities_and_handles_unquoted_uppercase_attributes()
    {
        const string Html = "<A HREF=/get?a=1&amp;b=2>Download</A><IMG SRC=../x.png>";

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal("https://example.com/get?a=1&b=2", result.Links[0].Url.AbsoluteUri);
        Assert.Equal("https://example.com/x.png", result.Links[1].Url.AbsoluteUri);
    }

    [Fact]
    public void Collect_classifies_href_src_srcset_media_and_poster()
    {
        const string Html = """
            <a href="a.zip">A</a>
            <img src="a.jpg" srcset="a-small.jpg 1x, a-large.jpg 2x">
            <video src="movie.mp4" poster="poster.jpg"><source src="movie.webm"></video>
            <audio src="song.m4a"></audio>
            """;

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal(8, result.Links.Count);
        Assert.Equal(CollectedLinkKind.Hyperlink, result.Links[0].Kind);
        Assert.Equal(CollectedLinkKind.Source, result.Links[1].Kind);
        Assert.Equal(CollectedLinkKind.SourceSet, result.Links[2].Kind);
        Assert.Equal(CollectedLinkKind.SourceSet, result.Links[3].Kind);
        Assert.Equal(CollectedLinkKind.MediaSource, result.Links[4].Kind);
        Assert.Equal(CollectedLinkKind.Poster, result.Links[5].Kind);
        Assert.Equal(CollectedLinkKind.MediaSource, result.Links[6].Kind);
        Assert.Equal(CollectedLinkKind.MediaSource, result.Links[7].Kind);
    }

    [Fact]
    public void Collect_skips_fake_tags_inside_script_style_and_comments()
    {
        const string Html = """
            <!-- <a href="comment-secret.zip">fake</a> -->
            <script src="real-script.js">
                const fake = '<a href="script-secret.zip">x</a>';
            </script>
            <style>.x { background: url("style-secret.png") } <a href="css-secret.zip"></style>
            <a href="real.zip">real</a>
            """;

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal(
            ["https://example.com/docs/real-script.js", "https://example.com/docs/real.zip"],
            result.Links.Select(static link => link.Url.AbsoluteUri));
    }

    [Fact]
    public void Collect_deduplicates_canonical_urls_at_first_occurrence()
    {
        const string Html = """
            <a href="https://EXAMPLE.com:443/file.zip#one">one</a>
            <source src="https://example.com/file.zip#two">
            """;

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        var link = Assert.Single(result.Links);
        Assert.Equal("https://example.com/file.zip", link.Url.AbsoluteUri);
        Assert.Equal(CollectedLinkKind.Hyperlink, link.Kind);
    }

    [Fact]
    public void Collect_ignores_fragment_only_and_unsafe_schemes_or_hosts()
    {
        const string Html = """
            <a href="#section">same document</a>
            <a href="javascript:alert(1)">js</a>
            <img src="data:image/png;base64,AAAA">
            <a href="ftp://example.com/file.zip">ftp</a>
            <a href="https://user:secret@example.com/private">credentials</a>
            <a href="http://127.0.0.1/admin">loopback</a>
            <a href="good.zip">good</a>
            """;

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal("https://example.com/docs/good.zip", Assert.Single(result.Links).Url.AbsoluteUri);
    }

    [Fact]
    public void Extension_filter_is_case_insensitive_and_supports_compound_suffixes()
    {
        const string Html = """
            <a href="one.ZIP?token=x">zip</a>
            <a href="two.tar.gz">tar</a>
            <a href="three.gz">gz</a>
            """;
        var options = new HtmlLinkCollectorOptions { AllowedExtensions = ["zip", ".tar.gz"] };

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl, options);

        Assert.Equal(2, result.Links.Count);
        Assert.EndsWith("one.ZIP?token=x", result.Links[0].Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("two.tar.gz", result.Links[1].Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_and_exclude_globs_apply_to_absolute_urls()
    {
        const string Html = """
            <a href="release-good.zip">good</a>
            <a href="release-debug.zip">debug</a>
            <a href="notes.txt">notes</a>
            """;
        var options = new HtmlLinkCollectorOptions
        {
            IncludePatterns = ["https://example.com/*release-?.ood.zip", "*release-good.zip"],
            ExcludePatterns = ["*debug*"],
        };

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl, options);

        Assert.Equal("https://example.com/docs/release-good.zip", Assert.Single(result.Links).Url.AbsoluteUri);
    }

    [Fact]
    public void Same_host_filter_uses_document_host_not_cross_origin_base_host()
    {
        const string Html = """
            <base href="https://cdn.example.net/files/">
            <a href="external.zip">external</a>
            <a href="https://example.com/local.zip">local</a>
            """;
        var options = new HtmlLinkCollectorOptions { SameHostOnly = true };

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl, options);

        Assert.Equal("https://example.com/local.zip", Assert.Single(result.Links).Url.AbsoluteUri);
    }

    [Fact]
    public void Result_limit_is_deterministic_and_reports_truncation()
    {
        const string Html = """
            <a href="1">1</a><a href="2">2</a><a href="2#again">duplicate</a><a href="3">3</a>
            """;
        var options = new HtmlLinkCollectorOptions { MaximumResults = 2 };

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl, options);

        Assert.Equal(["1", "2"], result.Links.Select(static link => link.Url.Segments[^1]));
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Unsafe_base_is_ignored_without_poisoning_relative_resolution()
    {
        const string Html = "<base href='http://localhost/private/'><a href='safe.zip'>safe</a>";

        var result = HtmlLinkCollector.Collect(Html, DocumentUrl);

        Assert.Equal(DocumentUrl, result.EffectiveBaseUrl);
        Assert.Equal("https://example.com/docs/safe.zip", result.Links[^1].Url.AbsoluteUri);
    }

    [Fact]
    public void Malformed_html_does_not_execute_or_throw()
    {
        const string Html = "<div <a href='lost.zip'><img src='kept.png'><script>'<a href=x>'";

        var exception = Record.Exception(() => HtmlLinkCollector.Collect(Html, DocumentUrl));

        Assert.Null(exception);
    }

    [Fact]
    public void Oversized_document_is_rejected_before_scanning()
    {
        var options = new HtmlLinkCollectorOptions { MaximumHtmlCharacters = 10 };

        Assert.Throws<ArgumentException>(() => HtmlLinkCollector.Collect(new string('x', 11), DocumentUrl, options));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    [InlineData(1_000_001, 100)]
    [InlineData(1, 50_000_001)]
    public void Invalid_limits_are_rejected(int maximumResults, int maximumHtmlCharacters)
    {
        var options = new HtmlLinkCollectorOptions
        {
            MaximumResults = maximumResults,
            MaximumHtmlCharacters = maximumHtmlCharacters,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => HtmlLinkCollector.Collect("", DocumentUrl, options));
    }
}
