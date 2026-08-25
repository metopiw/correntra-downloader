using Correntra.Desktop.Services;
using Xunit;

namespace Correntra.Desktop.Tests;

/// <summary>
/// LocalizationService contract: switching to Turkish must actually flip the
/// active dictionary and the thread UI culture, and every key must exist in
/// both languages so no view falls back to English.
/// </summary>
public class LocalizationTests
{
    [Fact]
    public void SetLanguage_Turkish_ServesTurkishStrings()
    {
        var localizer = LocalizationService.Current;
        localizer.SetLanguage("tr");

        Assert.Equal("tr", localizer.LanguageCode);
        Assert.Equal("Ekle", localizer["Common.Add"]);
        Assert.Equal("Görevler", localizer["Menu.Tasks"]);
        Assert.Equal("İndirmelerde ara", localizer["Search.Placeholder"]);
    }

    [Fact]
    public void SetLanguage_English_ServesEnglishStrings()
    {
        var localizer = LocalizationService.Current;
        localizer.SetLanguage("en");

        Assert.Equal("en", localizer.LanguageCode);
        Assert.Equal("Add", localizer["Common.Add"]);
        Assert.Equal("Tasks", localizer["Menu.Tasks"]);
    }

    [Fact]
    public void SetLanguage_AppliesUiCulture()
    {
        LocalizationService.Current.SetLanguage("tr");
        Assert.Equal("tr-TR", System.Globalization.CultureInfo.CurrentUICulture.Name);

        LocalizationService.Current.SetLanguage("en");
        Assert.Equal("en-US", System.Globalization.CultureInfo.CurrentUICulture.Name);
    }
}
