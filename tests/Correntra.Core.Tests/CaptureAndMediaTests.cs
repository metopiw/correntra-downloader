using Correntra.Core.Browser;
using Correntra.Core.Downloads;
using Correntra.Core.Media;
using Correntra.Core.Security;

namespace Correntra.Core.Tests;

public sealed class CaptureAndMediaTests
{
    [Fact]
    public void GetCaptureCanCreateDownloadSource()
    {
        BrowserDownloadCapture capture = new(
            new BrowserCaptureId("capture_12345678"),
            BrowserFamily.Chrome,
            new Uri("https://cdn.example.test/file.zip"),
            TestData.Timestamp,
            suggestedFileName: "folder/file.zip",
            contentLength: 42,
            referrer: new Uri("https://example.test/page"),
            requestHeaders: new HttpHeaderSet([new("User-Agent", "Browser")]));

        DownloadSource source = capture.ToDownloadSource();

        Assert.True(capture.CanReplaySafely);
        Assert.Equal("folder_file.zip", capture.SuggestedFileName);
        Assert.Equal(capture.Url, source.Url);
        Assert.Equal("Browser", source.Headers["User-Agent"]);
    }

    [Fact]
    public void PostCaptureIsNotConsideredSafeToReplay()
    {
        BrowserDownloadCapture capture = new(
            new BrowserCaptureId("capture_12345678"),
            BrowserFamily.Edge,
            new Uri("https://example.test/export"),
            TestData.Timestamp,
            method: DownloadRequestMethod.Post);

        Assert.False(capture.CanReplaySafely);
        Assert.Throws<InvalidOperationException>(() => capture.ToDownloadSource());
    }

    [Fact]
    public void AcceptedBrowserCaptureRequiresJobId()
    {
        BrowserCaptureId id = new("capture_12345678");

        Assert.Throws<ArgumentException>(() => new BrowserCaptureResult(id, BrowserCaptureDisposition.Accepted));
        BrowserCaptureResult accepted = new(id, BrowserCaptureDisposition.Accepted, JobId.Create());
        Assert.NotNull(accepted.JobId);
    }

    [Fact]
    public void CandidateSummaryDoesNotExposePrivilegedSourceUrl()
    {
        CapturedMediaCandidate candidate = CreateCandidate();

        MediaCandidateSummary summary = candidate.ToSummary();

        Assert.Equal(candidate.Id, summary.Id);
        Assert.Equal(candidate.Tracks, summary.Tracks);
        Assert.DoesNotContain(summary.GetType().GetProperties(), static property => property.PropertyType == typeof(Uri));
    }

    [Fact]
    public void DrmCandidateCannotCreateOrValidateDownloadSelection()
    {
        MediaTrack track = VideoTrack();
        CapturedMediaCandidate candidate = new(
            new MediaCandidateId("candidate_12345678"),
            MediaKind.Video,
            MediaTransport.Dash,
            "Protected video",
            new Uri("https://example.test/watch"),
            TestData.Source("https://cdn.example.test/manifest.mpd"),
            TestData.Timestamp,
            [track],
            protection: MediaProtection.DrmProtected,
            protectionReason: "Widevine ContentProtection");
        MediaSelectionRequest selection = new(candidate.Id, track.Id);

        Assert.False(candidate.CanCreateDownload);
        Assert.Throws<InvalidOperationException>(() => selection.ValidateAgainst(candidate));
    }

    [Fact]
    public void SelectionValidatesTrackKindsAndMembership()
    {
        CapturedMediaCandidate candidate = CreateCandidate();
        MediaTrack video = candidate.Tracks.Single(static track => track.Kind == MediaTrackKind.Video);
        MediaTrack audio = candidate.Tracks.Single(static track => track.Kind == MediaTrackKind.Audio);
        MediaSelectionRequest valid = new(candidate.Id, video.Id, audio.Id);
        valid.ValidateAgainst(candidate);

        MediaSelectionRequest missing = new(candidate.Id, new MediaTrackId("missing"), audio.Id);
        Assert.Throws<InvalidOperationException>(() => missing.ValidateAgainst(candidate));
    }

    [Fact]
    public void NonVideoTrackCannotHaveDimensions()
    {
        Assert.Throws<ArgumentException>(() => new MediaTrack(
            new MediaTrackId("audio"),
            MediaTrackKind.Audio,
            "Audio",
            width: 1920,
            height: 1080));
    }

    private static CapturedMediaCandidate CreateCandidate()
    {
        MediaTrack video = VideoTrack();
        MediaTrack audio = new(
            new MediaTrackId("audio_en"),
            MediaTrackKind.Audio,
            "English audio",
            language: "en",
            codec: "opus",
            bitrate: 128_000);
        return new CapturedMediaCandidate(
            new MediaCandidateId("candidate_12345678"),
            MediaKind.Video,
            MediaTransport.Dash,
            "Example video",
            new Uri("https://example.test/watch"),
            TestData.Source("https://cdn.example.test/manifest.mpd?token=secret"),
            TestData.Timestamp,
            [video, audio],
            duration: TimeSpan.FromMinutes(2));
    }

    private static MediaTrack VideoTrack()
    {
        return new MediaTrack(
            new MediaTrackId("video_1080p"),
            MediaTrackKind.Video,
            "1080p",
            codec: "avc1.640028",
            container: "mp4",
            width: 1920,
            height: 1080,
            bitrate: 4_000_000);
    }
}
