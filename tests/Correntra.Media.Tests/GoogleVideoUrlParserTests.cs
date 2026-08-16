using Correntra.Media.Models;
using Correntra.Media.Sites;

namespace Correntra.Media.Tests;

public sealed class GoogleVideoUrlParserTests
{
    [Fact]
    public void Parse_KnownVideoItag_ProducesQuality()
    {
        var uri = new Uri("https://rr1---sn.example.googlevideo.com/videoplayback?itag=137&mime=video%2Fmp4%3B+codecs%3D%22avc1.640028%22&clen=123456&bitrate=4200000");

        GoogleVideoInfo result = GoogleVideoUrlParser.Parse(uri);

        Assert.Equal(137, result.Itag);
        Assert.Equal(MediaTrackKind.Video, result.TrackKind);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal("1080p", result.DisplayName);
        Assert.Equal("video/mp4", result.MimeType);
        Assert.Equal(123456, result.ContentLength);
    }

    [Fact]
    public void Parse_KnownAudioItag_ProducesAudioTrack()
    {
        var uri = new Uri("https://r1.googlevideo.com/videoplayback?itag=251&mime=audio%2Fwebm&clen=9000");

        GoogleVideoInfo result = GoogleVideoUrlParser.Parse(uri);

        Assert.Equal(MediaTrackKind.Audio, result.TrackKind);
        Assert.Equal("WEBM", result.Container);
    }

    [Theory]
    [InlineData("https://googlevideo.com/videoplayback", true)]
    [InlineData("https://r1.googlevideo.com/videoplayback", true)]
    [InlineData("https://evilgooglevideo.com/file", false)]
    public void IsGoogleVideo_ValidatesHostBoundary(string value, bool expected)
    {
        Assert.Equal(expected, GoogleVideoUrlParser.IsGoogleVideo(new Uri(value)));
    }
}

