using Correntra.Core.Security;

namespace Correntra.Core.Tests;

public sealed class HttpSecurityTests
{
    [Fact]
    public void HeaderLookupIsCaseInsensitiveAndImmutableFromInput()
    {
        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["User-Agent"] = "Correntra",
        };
        HttpHeaderSet headers = new(source);
        source["User-Agent"] = "mutated";

        Assert.Equal("Correntra", headers["user-agent"]);
        Assert.Equal(1, headers.Count);
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Bad:Header")]
    [InlineData("Ünicode")]
    [InlineData("")]
    public void RejectsInvalidHeaderNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new HttpHeaderSet([new(name, "value")]));
    }

    [Theory]
    [InlineData("value\r\nInjected: yes")]
    [InlineData("value\0secret")]
    [InlineData("value\u0001secret")]
    public void RejectsHeaderControlCharacterInjection(string value)
    {
        Assert.Throws<ArgumentException>(() => new HttpHeaderSet([new("X-Test", value)]));
    }

    [Fact]
    public void RejectsDuplicateHeadersIgnoringCase()
    {
        Assert.Throws<ArgumentException>(() => new HttpHeaderSet(
        [
            new("Cookie", "a=1"),
            new("cookie", "b=2"),
        ]));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("Custom-Token")]
    [InlineData("Client-Secret")]
    public void RecognizesCredentialHeaders(string name)
    {
        Assert.True(HttpHeaderSet.IsSensitiveName(name));
    }

    [Fact]
    public void RedactionReplacesOnlySensitiveHeaderValues()
    {
        HttpHeaderSet headers = new(
        [
            new("Cookie", "session=secret"),
            new("Authorization", "Bearer secret"),
            new("User-Agent", "Correntra"),
        ]);

        HttpHeaderSet redacted = headers.Redacted();

        Assert.Equal(HttpHeaderSet.RedactedValue, redacted["Cookie"]);
        Assert.Equal(HttpHeaderSet.RedactedValue, redacted["Authorization"]);
        Assert.Equal("Correntra", redacted["User-Agent"]);
        Assert.Equal("session=secret", headers["Cookie"]);
    }

    [Fact]
    public void UriRedactionRemovesUserInfoQueryAndFragment()
    {
        Uri uri = new("https://alice:password@example.test/video.m3u8?token=secret&quality=1080#fragment");

        string redacted = SensitiveDataRedactor.RedactUri(uri);

        Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("password", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("quality", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment", redacted, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.RedactedValue, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TextRedactionRemovesCommonSecrets()
    {
        const string text = "Authorization: Bearer abc Cookie: sid=xyz https://e.test/a?token=qwerty";

        string result = SensitiveDataRedactor.RedactText(text);

        Assert.DoesNotContain("abc", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sid=xyz", result, StringComparison.Ordinal);
        Assert.DoesNotContain("qwerty", result, StringComparison.Ordinal);
    }
}
