using Correntra.Agent.Runtime;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class YtDlpRoutingTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", true)]
    [InlineData("https://vk.com/video-1_2", true)]
    [InlineData("https://rumble.com/v123", true)]
    [InlineData("https://www.bilibili.com/video/BV1", true)]
    [InlineData("https://example.test/watch/1", false)]
    public void KnownPlatformHostsAreSupported(string url, bool expected)
    {
        Assert.Equal(expected, YtDlpExecutor.IsSupportedHost(new Uri(url)));
    }

    [Fact]
    public void DirectMp4OnUnknownHostStaysOnHttpEngine()
    {
        var source = new Uri("https://cdn.example.test/films/clip.mp4");
        var page = new Uri("https://example.test/watch/clip");
        Assert.True(YtDlpExecutor.LooksLikeDirectMedia(source));
        Assert.False(YtDlpExecutor.ShouldExtractWithYtDlp(source, page));
    }

    [Fact]
    public void YoutubeWatchPageExtractsEvenWhenSrcIsFragmentCdn()
    {
        var source = new Uri("https://rr1.sn-abc.googlevideo.com/videoplayback?id=1");
        var page = new Uri("https://www.youtube.com/watch?v=abc");
        Assert.True(YtDlpExecutor.IsFragmentCdn(source));
        Assert.True(YtDlpExecutor.ShouldExtractWithYtDlp(source, page));
    }

    [Fact]
    public void UnknownWatchPageStillTriesExtractor()
    {
        var page = new Uri("https://videos.example.test/watch?id=9");
        Assert.True(YtDlpExecutor.ShouldExtractWithYtDlp(page, page));
    }

    [Fact]
    public void QualityListPutsHighestVideoFirstAndAudioLast()
    {
        List<MediaQualityOption> ranked = AgentCommandDispatcher.RankQualities(
        [
            new MediaQualityOption("a", "Audio only", "audio", null, 128000, null),
            new MediaQualityOption("480", "480p", "mp4", 480, null, null),
            new MediaQualityOption("1080-dup", "1080p copy", "mp4", 1080, null, null),
            new MediaQualityOption("1080", "1080p", "mp4", 1080, 5000000, null),
            new MediaQualityOption("720", "720p", "mp4", 720, null, null),
        ]);

        Assert.Equal(["1080p", "720p", "480p", "Audio only"], ranked.Select(option => option.DisplayName).ToArray());
    }
}
