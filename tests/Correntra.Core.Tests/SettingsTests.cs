using Correntra.Core.Settings;

namespace Correntra.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void DefaultsMatchPrivacyAndProductDecisions()
    {
        ApplicationSettings settings = ApplicationSettings.CreateDefault(TestData.DestinationDirectory);

        Assert.Equal(SupportedLanguage.Turkish, settings.General.Language);
        Assert.Equal(ApplicationTheme.Dark, settings.General.Theme);
        Assert.True(settings.BrowserIntegration.IsEnabled);
        Assert.True(settings.BrowserIntegration.ShareSiteSessions);
        Assert.False(PrivacySettings.TelemetryEnabled);
        Assert.Equal(UpdatePreference.DownloadWithConfirmation, settings.Updates.Preference);
    }

    [Fact]
    public void PortableDistributionCanOnlyNotify()
    {
        GitHubRepository repository = new("owner", "repo");

        Assert.Throws<ArgumentException>(() => new UpdateSettings(
            repository,
            DistributionMode.Portable,
            UpdatePreference.DownloadWithConfirmation));

        UpdateSettings valid = new(repository, DistributionMode.Portable, UpdatePreference.NotifyOnly);
        Assert.Equal(UpdatePreference.NotifyOnly, valid.Preference);
    }

    [Theory]
    [InlineData("tr", SupportedLanguage.Turkish)]
    [InlineData("tr-TR", SupportedLanguage.Turkish)]
    [InlineData("en-US", SupportedLanguage.English)]
    public void MapsSupportedCultures(string culture, SupportedLanguage expected)
    {
        Assert.Equal(expected, SupportedLanguageExtensions.FromCultureName(culture));
    }

    [Fact]
    public void BrowserExclusionsSupportWildcardSubdomains()
    {
        BrowserIntegrationSettings settings = new(excludedHosts: ["*.example.test"]);

        Assert.True(settings.IsHostExcluded("media.example.test"));
        Assert.False(settings.IsHostExcluded("example.test"));
        Assert.False(settings.IsHostExcluded("notexample.test"));
    }

    [Fact]
    public void GitHubRepositoryRoundTripsOwnerName()
    {
        GitHubRepository repository = GitHubRepository.Parse("Correntra/downloader");

        Assert.Equal("Correntra", repository.Owner);
        Assert.Equal("downloader", repository.Name);
        Assert.Equal("Correntra/downloader", repository.ToString());
    }
}
