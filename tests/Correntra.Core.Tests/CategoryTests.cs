using Correntra.Core.Categories;

namespace Correntra.Core.Tests;

public sealed class CategoryTests
{
    [Fact]
    public void DefaultCategoriesContainCommittedFiveCategories()
    {
        string[] names = DefaultDownloadCategories.All.Select(static category => category.Name).ToArray();

        Assert.Equal(["Compressed", "Documents", "Music", "Programs", "Video"], names);
        Assert.All(DefaultDownloadCategories.All, static category => Assert.True(category.IsBuiltIn));
    }

    [Fact]
    public void DefaultRoutingUsesExtensionOrContentType()
    {
        CategoryRouter router = new(DefaultDownloadCategories.All);

        DownloadCategory? video = router.Route(new CategoryMatchContext(
            new Uri("https://cdn.example.test/opaque"),
            contentType: "video/mp4; charset=binary"));
        DownloadCategory? music = router.Route(new CategoryMatchContext(
            new Uri("https://cdn.example.test/song.MP3"),
            "song.MP3"));

        Assert.Equal(DefaultDownloadCategories.VideoId, video?.Id);
        Assert.Equal(DefaultDownloadCategories.MusicId, music?.Id);
    }

    [Fact]
    public void HighPriorityUserRuleOverridesDefaultRouting()
    {
        CategoryRule rule = new(
            CategoryRuleId.Create(),
            DefaultDownloadCategories.DocumentsId,
            priority: 100,
            sitePattern: "*.example.test",
            fileExtensions: ["mp4"]);
        CategoryRouter router = new(DefaultDownloadCategories.All, [rule]);

        DownloadCategory? result = router.Route(new CategoryMatchContext(
            new Uri("https://media.example.test/movie.mp4"),
            "movie.mp4",
            "video/mp4"));

        Assert.Equal(DefaultDownloadCategories.DocumentsId, result?.Id);
    }

    [Fact]
    public void WildcardHostDoesNotMatchApexDomain()
    {
        CategoryRule rule = new(
            CategoryRuleId.Create(),
            DefaultDownloadCategories.VideoId,
            0,
            sitePattern: "*.example.test");

        Assert.False(rule.Matches(new CategoryMatchContext(new Uri("https://example.test/file"))));
        Assert.True(rule.Matches(new CategoryMatchContext(new Uri("https://www.example.test/file"))));
    }

    [Fact]
    public void DisabledRuleNeverMatches()
    {
        CategoryRule rule = new(
            CategoryRuleId.Create(),
            DefaultDownloadCategories.VideoId,
            0,
            sitePattern: "example.test",
            isEnabled: false);

        Assert.False(rule.Matches(new CategoryMatchContext(new Uri("https://example.test/video"))));
    }

    [Fact]
    public void RuleRequiresCriterionAndKnownTarget()
    {
        Assert.Throws<ArgumentException>(() => new CategoryRule(
            CategoryRuleId.Create(),
            DefaultDownloadCategories.VideoId,
            0));

        CategoryRule unknownTarget = new(
            CategoryRuleId.Create(),
            CategoryId.Create(),
            0,
            fileExtensions: ["mp4"]);
        Assert.Throws<ArgumentException>(() => new CategoryRouter(DefaultDownloadCategories.All, [unknownTarget]));
    }
}
