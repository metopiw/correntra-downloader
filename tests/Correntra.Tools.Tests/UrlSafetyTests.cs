using System.Net;
using System.Net.Sockets;
using Correntra.Tools;

namespace Correntra.Tools.Tests;

public sealed class UrlSafetyTests
{
    [Fact]
    public void Evaluate_rejects_null_and_relative_urls()
    {
        Assert.Equal(UrlRejectionReason.NotAbsolute, UrlGuard.Evaluate(null).Reason);
        Assert.Equal(UrlRejectionReason.NotAbsolute, UrlGuard.Evaluate(new Uri("relative", UriKind.Relative)).Reason);
    }

    [Theory]
    [InlineData("ftp://example.com/file", UrlRejectionReason.UnsupportedScheme)]
    [InlineData("file:///c:/secret.txt", UrlRejectionReason.UnsupportedScheme)]
    [InlineData("https://user:secret@example.com/file", UrlRejectionReason.EmbeddedCredentials)]
    [InlineData("http://localhost/file", UrlRejectionReason.LocalHost)]
    [InlineData("http://api.localhost/file", UrlRejectionReason.LocalHost)]
    [InlineData("http://printer.local/file", UrlRejectionReason.LocalHost)]
    public void Evaluate_rejects_unsafe_url_shapes(string value, UrlRejectionReason expected)
    {
        Assert.Equal(expected, UrlGuard.Evaluate(new Uri(value)).Reason);
    }

    [Theory]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.2.3.4/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://127.10.1.1/")]
    [InlineData("http://169.254.4.2/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://192.0.2.1/")]
    [InlineData("http://192.88.99.1/")]
    [InlineData("http://198.18.1.1/")]
    [InlineData("http://203.0.113.5/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[2001:db8::1]/")]
    public void Evaluate_rejects_non_public_literal_addresses(string value)
    {
        Assert.Equal(UrlRejectionReason.NonPublicAddress, UrlGuard.Evaluate(new Uri(value)).Reason);
    }

    [Theory]
    [InlineData("https://example.com/file")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://192.0.1.1/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    public void Evaluate_accepts_public_http_urls(string value)
    {
        Assert.True(UrlGuard.Evaluate(new Uri(value)).IsAllowed);
    }

    [Fact]
    public void Policy_can_explicitly_allow_credentials_and_local_destinations()
    {
        var policy = new UrlSafetyPolicy
        {
            AllowEmbeddedCredentials = true,
            AllowLocalHostNames = true,
            AllowNonPublicAddresses = true,
        };

        Assert.True(UrlGuard.Evaluate(new Uri("http://user:pass@localhost/file"), policy).IsAllowed);
        Assert.True(UrlGuard.Evaluate(new Uri("http://192.168.1.4/file"), policy).IsAllowed);
    }

    [Fact]
    public void Canonicalize_removes_fragment_and_default_port()
    {
        var result = UrlGuard.Canonicalize(new Uri("HTTPS://Example.COM:443/a/../b?q=1#part"));

        Assert.Equal("https://example.com/b?q=1", result.AbsoluteUri);
    }

    [Fact]
    public async Task Request_validation_rejects_host_if_any_dns_result_is_private()
    {
        var resolver = new StubResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.4"));

        var result = await UrlGuard.EvaluateForRequestAsync(new Uri("https://example.com/a"), resolver);

        Assert.Equal(UrlRejectionReason.NonPublicAddress, result.Reason);
    }

    [Fact]
    public async Task Request_validation_accepts_only_public_dns_results()
    {
        var resolver = new StubResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700::1111"));

        var result = await UrlGuard.EvaluateForRequestAsync(new Uri("https://example.com/a"), resolver);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Request_validation_rejects_empty_or_failed_dns_results()
    {
        var empty = await UrlGuard.EvaluateForRequestAsync(new Uri("https://example.com"), new StubResolver());
        var failed = await UrlGuard.EvaluateForRequestAsync(
            new Uri("https://example.com"),
            new ThrowingResolver(new SocketException()));

        Assert.Equal(UrlRejectionReason.DnsResolutionFailed, empty.Reason);
        Assert.Equal(UrlRejectionReason.DnsResolutionFailed, failed.Reason);
    }

    [Fact]
    public async Task Request_validation_does_not_resolve_literal_ip()
    {
        var resolver = new ThrowingResolver(new InvalidOperationException("Must not be called."));

        var result = await UrlGuard.EvaluateForRequestAsync(new Uri("https://8.8.8.8"), resolver);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Request_validation_propagates_cancellation()
    {
        var resolver = new ThrowingResolver(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await UrlGuard.EvaluateForRequestAsync(new Uri("https://example.com"), resolver));
    }

    private sealed class StubResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string hostName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class ThrowingResolver(Exception exception) : IHostAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string hostName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<IPAddress>>(exception);
    }
}
