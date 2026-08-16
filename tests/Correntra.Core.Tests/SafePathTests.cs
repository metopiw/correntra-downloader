using System.Text;
using Correntra.Core.Security;

namespace Correntra.Core.Tests;

public sealed class SafePathTests
{
    [Theory]
    [InlineData("movie.mp4")]
    [InlineData("Türkçe şarkı.flac")]
    [InlineData("archive.tar.gz")]
    [InlineData("name-with spaces.txt")]
    public void AcceptsPortableComponents(string value)
    {
        Assert.True(SafePath.IsValidComponent(value));
        Assert.Equal(value, SafePath.ValidateComponent(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("stream:secret")]
    [InlineData("bad?.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT9.log")]
    [InlineData("COM¹.txt")]
    [InlineData("CLOCK$")]
    public void RejectsUnsafeOrWindowsReservedComponents(string value)
    {
        Assert.False(SafePath.IsValidComponent(value));
        Assert.Throws<ArgumentException>(() => SafePath.ValidateComponent(value));
    }

    [Fact]
    public void RejectsNonCanonicalUnicode()
    {
        string decomposed = "e\u0301.txt";

        Assert.Equal(decomposed.Normalize(NormalizationForm.FormC), SafePath.SanitizeFileName(decomposed));
        Assert.False(SafePath.IsValidComponent(decomposed));
    }

    [Theory]
    [InlineData("../../CON?.mp4", ".._.._CON_.mp4")]
    [InlineData("folder\\video:one.mp4", "folder_video_one.mp4")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("   ", "download")]
    public void SanitizesUntrustedNames(string input, string expected)
    {
        string result = SafePath.SanitizeFileName(input);

        Assert.Equal(expected, result);
        Assert.True(SafePath.IsValidComponent(result));
    }

    [Fact]
    public void TruncationPreservesShortExtension()
    {
        string result = SafePath.SanitizeFileName(new string('a', 100) + ".mkv", maximumLength: 32);

        Assert.Equal(32, result.Length);
        Assert.EndsWith(".mkv", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinesValidatedComponentsUnderCanonicalRoot()
    {
        string result = SafePath.CombineUnderRoot(TestData.DestinationDirectory, "Video", "movie.mp4");

        Assert.True(Path.IsPathFullyQualified(result));
        Assert.EndsWith(Path.Combine("Video", "movie.mp4"), result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("C:\\Windows")]
    [InlineData("folder/subfolder")]
    public void CombineRejectsTraversalAndRootedComponents(string component)
    {
        Assert.Throws<ArgumentException>(() => SafePath.CombineUnderRoot(TestData.DestinationDirectory, component));
    }

    [Fact]
    public void CanonicalizeDirectoryRejectsNullCharacters()
    {
        Assert.Throws<ArgumentException>(() => SafePath.CanonicalizeDirectory("safe\0unsafe"));
    }
}
