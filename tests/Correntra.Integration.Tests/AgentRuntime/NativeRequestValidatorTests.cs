using System.Text.Json;
using Correntra.Core;
using Correntra.NativeHost.Protocol;

namespace Correntra.Integration.Tests.AgentRuntime;

public sealed class NativeRequestValidatorTests
{
    [Fact]
    public void AcceptsExtensionTakeoverEnvelope()
    {
        using JsonDocument document = Parse("""
            {
              "protocolVersion": 1,
              "kind": "takeover.offer",
              "requestId": "r_1234567890",
              "timestampUtc": "2026-08-13T17:01:02.345Z",
              "payload": {
                "url": "https://example.test/file.zip",
                "headers": { "Referer": "https://example.test/page" }
              }
            }
            """);

        NativeRequestEnvelope result = NativeRequestValidator.Validate(document.RootElement);

        Assert.Equal("takeover.offer", result.Kind);
        Assert.Equal("r_1234567890", result.RequestId);
    }

    [Theory]
    [InlineData("ftp://example.test/file.zip")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("https://user:password@example.test/file.zip")]
    public void RejectsNonHttpOrCredentialBearingUrls(string url)
    {
        using JsonDocument document = Parse($$"""
            {
              "protocolVersion": 1,
              "kind": "takeover.offer",
              "requestId": "r_url",
              "timestampUtc": "2026-08-13T17:01:02.345Z",
              "payload": { "url": "{{url}}" }
            }
            """);

        Assert.Throws<InvalidDataException>(() => NativeRequestValidator.Validate(document.RootElement));
    }

    [Fact]
    public void RejectsCrlfInHeaderValue()
    {
        using JsonDocument document = Parse("""
            {
              "protocolVersion": 1,
              "kind": "takeover.offer",
              "requestId": "r_header",
              "timestampUtc": "2026-08-13T17:01:02.345Z",
              "payload": {
                "url": "https://example.test/file.zip",
                "headers": { "Referer": "ok\r\nX-Evil: yes" }
              }
            }
            """);

        Assert.Throws<InvalidDataException>(() => NativeRequestValidator.Validate(document.RootElement));
    }

    [Fact]
    public void RejectsUnexpectedEnvelopeProperty()
    {
        using JsonDocument document = Parse("""
            {
              "protocolVersion": 1,
              "kind": "host.ping",
              "requestId": "r_extra",
              "timestampUtc": "2026-08-13T17:01:02.345Z",
              "payload": {},
              "extra": true
            }
            """);

        Assert.Throws<InvalidDataException>(() => NativeRequestValidator.Validate(document.RootElement));
    }

    [Fact]
    public void AcceptsOnlyPinnedExtensionOrigin()
    {
        // Only the canonical pinned ID is accepted (the manifest's fixed key
        // makes this ID stable across unpacked installs).
        NativeRequestValidator.ValidateCallerOrigin(BrowserExtensionIdentity.ExtensionOrigin);

        // Any other well-formed extension origin — including the historical
        // development-path-derived ID and near-miss spellings — is rejected.
        Assert.Throws<UnauthorizedAccessException>(() =>
            NativeRequestValidator.ValidateCallerOrigin("chrome-extension://fbngehclfngjenhlchnkojooliaifggj/"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            NativeRequestValidator.ValidateCallerOrigin("chrome-extension://ddkjiahejlhfcafbddmgiahcphecmpfh/"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            NativeRequestValidator.ValidateCallerOrigin("https://example.test/"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            NativeRequestValidator.ValidateCallerOrigin("chrome-extension://bhnibkknmmodoehpaeoijnkabfdmbdjp"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            NativeRequestValidator.ValidateCallerOrigin("chrome-extension://zzzz/"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            NativeRequestValidator.ValidateCallerOrigin("chrome-extension://bhnibkknmmodoehpaeoijnkabfdmbdjz/"));
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}

