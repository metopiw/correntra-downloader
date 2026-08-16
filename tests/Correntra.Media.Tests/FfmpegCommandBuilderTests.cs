using Correntra.Media.Processing;

namespace Correntra.Media.Tests;

public sealed class FfmpegCommandBuilderTests
{
    [Fact]
    public void InspectorAcceptsSeparateLgplReports()
    {
        FfmpegInspectionResult result = FfmpegInspector.EvaluateReportedConfiguration(
            "ffmpeg version 8.1.2",
            "configuration: --enable-version3 --enable-shared --disable-static",
            "GNU Lesser General Public License version 3 or later",
            true);

        Assert.True(result.IsUsable);
        Assert.True(result.IsLgplCompatible);
        Assert.Null(result.FailureReason);
    }

    [Theory]
    [InlineData("--enable-gpl", "GPL")]
    [InlineData("--enable-nonfree", "nonfree")]
    public void InspectorRejectsForbiddenBuildFlags(string flag, string expectedReason)
    {
        FfmpegInspectionResult result = FfmpegInspector.EvaluateReportedConfiguration(
            "ffmpeg version 8.1.2",
            "configuration: --enable-version3 " + flag,
            "GNU Lesser General Public License version 3 or later",
            true);

        Assert.False(result.IsLgplCompatible);
        Assert.Contains(expectedReason, result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMux_UsesArgumentListWithoutShellQuoting()
    {
        IReadOnlyList<string> result = FfmpegCommandBuilder.BuildMux(
            @"C:\media folder\video.mp4",
            @"C:\media folder\audio.m4a",
            @"C:\output folder\done.mp4");

        Assert.Contains(@"C:\media folder\video.mp4", result);
        Assert.Contains(@"C:\media folder\audio.m4a", result);
        Assert.Contains("copy", result);
        Assert.DoesNotContain(result, argument => argument.Contains('"'));
    }

    [Fact]
    public void BuildAudioConversion_RejectsInvalidBitrate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegCommandBuilder.BuildAudioConversion("in", "out", AudioOutputFormat.Mp3, 4));
    }

    [Fact]
    public void BuildAudioConversion_AddsOnlySafeMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["title"] = "Correntra sample",
            ["bad key"] = "ignored",
        };

        IReadOnlyList<string> result = FfmpegCommandBuilder.BuildAudioConversion(
            "input.m4a",
            "output.mp3",
            AudioOutputFormat.Mp3,
            192,
            metadata);

        Assert.Contains("title=Correntra sample", result);
        Assert.DoesNotContain("bad key=ignored", result);
    }
}
