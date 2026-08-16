using System.Security.Cryptography;
using Correntra.Media.Hls;
using Correntra.Media.Models;

namespace Correntra.Media.Tests;

public sealed class HlsParserTests
{
    private static readonly Uri ManifestUri = new("https://media.example.test/path/master.m3u8?token=secret");

    [Fact]
    public void Parse_MasterPlaylist_ResolvesVariantsAndRenditions()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="Türkçe, Stereo",LANGUAGE="tr",DEFAULT=YES,AUTOSELECT=YES,URI="audio/tr.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",URI="../subs/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5400000,AVERAGE-BANDWIDTH=4900000,RESOLUTION=1920x1080,FRAME-RATE=59.94,CODECS="avc1.64002a,mp4a.40.2",AUDIO="audio",SUBTITLES="subs"
            video/1080.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=1800000,RESOLUTION=1280x720
            https://cdn.example.test/720.m3u8
            """;

        HlsPlaylist result = HlsParser.Parse(ManifestUri, manifest);

        Assert.True(result.IsMaster);
        Assert.Equal(2, result.Variants.Count);
        Assert.Equal(new Uri("https://media.example.test/path/video/1080.m3u8"), result.Variants[0].SourceUri);
        Assert.Equal(1920, result.Variants[0].Width);
        Assert.Equal(1080, result.Variants[0].Height);
        Assert.Equal("avc1.64002a,mp4a.40.2", result.Variants[0].Codecs);
        Assert.Equal(2, result.Renditions.Count);
        Assert.Equal("Türkçe, Stereo", result.Renditions[0].Name);
        Assert.True(result.Renditions[0].IsDefault);
        Assert.Equal(new Uri("https://media.example.test/subs/en.m3u8"), result.Renditions[1].SourceUri);
    }

    [Fact]
    public void Parse_VodMediaPlaylist_TracksRangesMapAndDiscontinuity()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:42
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="1000@0"
            #EXTINF:5.5,first
            #EXT-X-BYTERANGE:500@1000
            file.mp4
            #EXT-X-DISCONTINUITY
            #EXTINF:6.0,second
            #EXT-X-BYTERANGE:600
            file.mp4
            #EXT-X-ENDLIST
            """;

        HlsPlaylist result = HlsParser.Parse(ManifestUri, manifest);

        Assert.False(result.IsMaster);
        Assert.False(result.IsLive);
        Assert.Equal(42, result.MediaSequence);
        Assert.Equal(TimeSpan.FromSeconds(6), result.TargetDuration);
        Assert.NotNull(result.InitializationSegment);
        Assert.Equal(1000, result.InitializationSegment.ByteRangeLength);
        Assert.Collection(
            result.Segments,
            first =>
            {
                Assert.Equal(42, first.Sequence);
                Assert.Equal(1000, first.ByteRangeStart);
                Assert.Equal(500, first.ByteRangeLength);
                Assert.False(first.Discontinuity);
            },
            second =>
            {
                Assert.Equal(43, second.Sequence);
                Assert.Equal(1500, second.ByteRangeStart);
                Assert.Equal(600, second.ByteRangeLength);
                Assert.True(second.Discontinuity);
            });
    }

    [Fact]
    public void Parse_LivePlaylist_WithoutEndListIsLive()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXTINF:4,
            segment-1.ts
            """;

        HlsPlaylist result = HlsParser.Parse(ManifestUri, manifest);

        Assert.True(result.IsLive);
        Assert.False(result.HasEndList);
    }

    [Fact]
    public void Parse_Aes128Identity_IsSupportedClearEncryption()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x0000000000000000000000000000002A
            #EXTINF:4,
            segment.ts
            #EXT-X-ENDLIST
            """;

        HlsPlaylist result = HlsParser.Parse(ManifestUri, manifest);

        Assert.Equal(MediaProtection.ClearAes128, result.Protection);
        MediaEncryption encryption = Assert.IsType<MediaEncryption>(result.Segments[0].Encryption);
        Assert.Equal(new Uri("https://media.example.test/path/key.bin"), encryption.KeyUri);
        Assert.Equal(16, encryption.InitializationVector?.Length);
    }

    [Theory]
    [InlineData("SAMPLE-AES", null)]
    [InlineData("AES-128", "com.apple.streamingkeydelivery")]
    public void Parse_ProtectedEncryption_IsClassifiedAsDrm(string method, string? keyFormat)
    {
        string format = keyFormat is null ? string.Empty : $",KEYFORMAT=\"{keyFormat}\"";
        string manifest = $"#EXTM3U\n#EXT-X-KEY:METHOD={method},URI=\"key\"{format}\n#EXTINF:4,\nsegment.ts\n#EXT-X-ENDLIST";

        HlsPlaylist result = HlsParser.Parse(ManifestUri, manifest);

        Assert.Equal(MediaProtection.Drm, result.Protection);
        Assert.NotNull(result.ProtectionReason);
    }

    [Fact]
    public void Parse_InvalidHeader_Throws()
    {
        Assert.Throws<FormatException>(() => HlsParser.Parse(ManifestUri, "not a playlist"));
    }

    [Fact]
    public void Aes128Decryptor_DecryptsWithSequenceDerivedIv()
    {
        byte[] key = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
        byte[] plaintext = "Correntra HLS segment"u8.ToArray();
        byte[] iv = HlsAes128Decryptor.DeriveIv(42);
        byte[] encrypted;
        using (Aes aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        byte[] actual = HlsAes128Decryptor.Decrypt(encrypted, key, [], 42);

        Assert.Equal(plaintext, actual);
    }
}
