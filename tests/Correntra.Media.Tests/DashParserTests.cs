using Correntra.Media.Dash;
using Correntra.Media.Models;

namespace Correntra.Media.Tests;

public sealed class DashParserTests
{
    private static readonly Uri ManifestUri = new("https://media.example.test/root/manifest.mpd");

    [Fact]
    public void Parse_StaticTemplateTimeline_ExpandsAudioAndVideo()
    {
        const string manifest = """
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT12S">
              <BaseURL>cdn/</BaseURL>
              <Period duration="PT12S">
                <AdaptationSet contentType="video" mimeType="video/mp4" codecs="avc1.640028">
                  <SegmentTemplate timescale="1000" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number%05d$.m4s" startNumber="7">
                    <SegmentTimeline><S t="0" d="4000" r="2" /></SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v1080" bandwidth="5000000" width="1920" height="1080" frameRate="30000/1001" />
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="tr">
                  <Representation id="a1" bandwidth="128000">
                    <SegmentList timescale="1" duration="6" startNumber="1">
                      <Initialization sourceURL="audio/init.mp4" range="0-999" />
                      <SegmentURL media="audio/1.m4s" mediaRange="1000-1999" />
                      <SegmentURL media="audio/2.m4s" />
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest result = DashParser.Parse(ManifestUri, manifest);

        Assert.False(result.IsDynamic);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Duration);
        Assert.Collection(
            result.Representations,
            video =>
            {
                Assert.Equal(MediaTrackKind.Video, video.TrackKind);
                Assert.Equal(3, video.Segments.Count);
                Assert.Equal(new Uri("https://media.example.test/root/cdn/v1080/00007.m4s"), video.Segments[0].SourceUri);
                Assert.Equal(new Uri("https://media.example.test/root/cdn/v1080/init.mp4"), video.InitializationSegment?.SourceUri);
                Assert.NotNull(video.FrameRate);
                Assert.Equal(30000d / 1001d, video.FrameRate.Value, 5);
            },
            audio =>
            {
                Assert.Equal(MediaTrackKind.Audio, audio.TrackKind);
                Assert.Equal("tr", audio.Language);
                Assert.Equal(2, audio.Segments.Count);
                Assert.Equal(1000, audio.Segments[0].ByteRangeStart);
                Assert.Equal(1000, audio.Segments[0].ByteRangeLength);
            });
    }

    [Fact]
    public void Parse_DurationTemplate_ComputesSegmentCount()
    {
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT10S">
              <Period>
                <AdaptationSet mimeType="video/webm">
                  <SegmentTemplate timescale="1" duration="4" media="chunk-$Number$.webm" startNumber="1" />
                  <Representation id="video" bandwidth="900000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest result = DashParser.Parse(ManifestUri, manifest);

        Assert.Equal(3, result.Representations.Single().Segments.Count);
        Assert.EndsWith("chunk-3.webm", result.Representations.Single().Segments[2].SourceUri.AbsolutePath);
    }

    [Fact]
    public void Parse_NegativeTimelineRepeat_UsesNextStart()
    {
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT10S">
              <Period>
                <AdaptationSet contentType="video">
                  <SegmentTemplate timescale="1" media="$Time$.m4s">
                    <SegmentTimeline><S t="0" d="2" r="-1" /><S t="8" d="2" /></SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        DashManifest result = DashParser.Parse(ManifestUri, manifest);

        Assert.Equal(["0.m4s", "2.m4s", "4.m4s", "6.m4s", "8.m4s"],
            result.Representations.Single().Segments.Select(segment => Path.GetFileName(segment.SourceUri.AbsolutePath)));
    }

    [Fact]
    public void Parse_ContentProtection_IsDrm()
    {
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period><AdaptationSet mimeType="video/mp4">
                <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" value="Widevine" />
                <Representation id="v"><BaseURL>protected.mp4</BaseURL></Representation>
              </AdaptationSet></Period>
            </MPD>
            """;

        DashManifest result = DashParser.Parse(ManifestUri, manifest);

        Assert.Equal(MediaProtection.Drm, result.Protection);
        Assert.Equal(MediaProtection.Drm, result.Representations.Single().Protection);
        Assert.Contains("ContentProtection", result.ProtectionReason);
    }

    [Fact]
    public void Parse_DynamicManifest_ExposesLiveMetadata()
    {
        const string manifest = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic" minimumUpdatePeriod="PT5S" availabilityStartTime="2026-08-13T12:00:00Z">
              <Period><AdaptationSet><Representation id="v"><BaseURL>live.mp4</BaseURL></Representation></AdaptationSet></Period>
            </MPD>
            """;

        DashManifest result = DashParser.Parse(ManifestUri, manifest);

        Assert.True(result.IsDynamic);
        Assert.Equal(TimeSpan.FromSeconds(5), result.MinimumUpdatePeriod);
        Assert.Equal(DateTimeOffset.Parse("2026-08-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), result.AvailabilityStartTime);
    }

    [Fact]
    public void Parse_InvalidXml_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => DashParser.Parse(ManifestUri, "<MPD>"));
    }
}
