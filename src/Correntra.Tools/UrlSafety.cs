using System.Net;
using System.Net.Sockets;

namespace Correntra.Tools;

/// <summary>Explains why a URL was rejected before a request is made.</summary>
public enum UrlRejectionReason
{
    /// <summary>The URL passed all requested checks.</summary>
    None,

    /// <summary>The value is not an absolute URL.</summary>
    NotAbsolute,

    /// <summary>The URL does not use HTTP or HTTPS.</summary>
    UnsupportedScheme,

    /// <summary>The URL embeds a user name or password.</summary>
    EmbeddedCredentials,

    /// <summary>The host is empty or syntactically invalid.</summary>
    InvalidHost,

    /// <summary>The host names the local computer or a local-only DNS domain.</summary>
    LocalHost,

    /// <summary>The literal or resolved address is private, link-local, reserved, or otherwise non-public.</summary>
    NonPublicAddress,

    /// <summary>The host could not be resolved safely.</summary>
    DnsResolutionFailed,
}

/// <summary>Contains the result of validating a URL for collection or network use.</summary>
/// <param name="IsAllowed">Whether the URL passed validation.</param>
/// <param name="Reason">The machine-readable rejection reason.</param>
public readonly record struct UrlSafetyResult(bool IsAllowed, UrlRejectionReason Reason)
{
    /// <summary>Gets a successful validation result.</summary>
    public static UrlSafetyResult Allowed { get; } = new(true, UrlRejectionReason.None);

    /// <summary>Creates a rejected validation result.</summary>
    /// <param name="reason">The reason for rejecting the URL.</param>
    /// <returns>A rejected result.</returns>
    public static UrlSafetyResult Rejected(UrlRejectionReason reason) => new(false, reason);
}

/// <summary>Controls exceptional URL destinations that the caller explicitly trusts.</summary>
public sealed record UrlSafetyPolicy
{
    /// <summary>Gets a strict internet-only policy.</summary>
    public static UrlSafetyPolicy Strict { get; } = new();

    /// <summary>Gets or initializes whether embedded URL credentials are accepted.</summary>
    public bool AllowEmbeddedCredentials { get; init; }

    /// <summary>Gets or initializes whether localhost and local-only DNS names are accepted.</summary>
    public bool AllowLocalHostNames { get; init; }

    /// <summary>Gets or initializes whether non-public literal or resolved IP addresses are accepted.</summary>
    public bool AllowNonPublicAddresses { get; init; }
}

/// <summary>Resolves a host name so every destination address can be checked before connecting.</summary>
public interface IHostAddressResolver
{
    /// <summary>Resolves all currently advertised addresses for a host.</summary>
    /// <param name="hostName">The ASCII DNS host name.</param>
    /// <param name="cancellationToken">Stops the operation.</param>
    /// <returns>The resolved addresses.</returns>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostName,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses the operating system DNS resolver.</summary>
public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        return await Dns.GetHostAddressesAsync(hostName, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Normalizes and validates web URLs before they enter a download or crawl queue.</summary>
public static class UrlGuard
{
    /// <summary>Checks syntax, scheme, credentials, and literal/local destinations without performing DNS.</summary>
    /// <param name="url">The absolute URL to inspect.</param>
    /// <param name="policy">Optional exceptions; strict internet-only behavior is the default.</param>
    /// <returns>The validation result.</returns>
    public static UrlSafetyResult Evaluate(Uri? url, UrlSafetyPolicy? policy = null)
    {
        policy ??= UrlSafetyPolicy.Strict;

        if (url is null || !url.IsAbsoluteUri)
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.NotAbsolute);
        }

        if (!url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.UnsupportedScheme);
        }

        if (!policy.AllowEmbeddedCredentials && !string.IsNullOrEmpty(url.UserInfo))
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.EmbeddedCredentials);
        }

        if (string.IsNullOrWhiteSpace(url.Host))
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.InvalidHost);
        }

        if (!policy.AllowLocalHostNames && IsLocalHostName(url.DnsSafeHost))
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.LocalHost);
        }

        if (IPAddress.TryParse(url.DnsSafeHost, out var address)
            && !policy.AllowNonPublicAddresses
            && !IsPublicInternetAddress(address))
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.NonPublicAddress);
        }

        return UrlSafetyResult.Allowed;
    }

    /// <summary>
    /// Validates a URL for an outbound request, including every DNS result. Callers should still pin the
    /// validated addresses at connect time to close DNS-rebinding time-of-check/time-of-use gaps.
    /// </summary>
    /// <param name="url">The absolute URL to inspect.</param>
    /// <param name="resolver">The DNS resolver to use.</param>
    /// <param name="policy">Optional exceptions; strict internet-only behavior is the default.</param>
    /// <param name="cancellationToken">Stops DNS validation.</param>
    /// <returns>The validation result.</returns>
    public static async ValueTask<UrlSafetyResult> EvaluateForRequestAsync(
        Uri? url,
        IHostAddressResolver resolver,
        UrlSafetyPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        policy ??= UrlSafetyPolicy.Strict;
        var initial = Evaluate(url, policy);
        if (!initial.IsAllowed || url is null || IPAddress.TryParse(url.DnsSafeHost, out _))
        {
            return initial;
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(url.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException)
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.DnsResolutionFailed);
        }

        if (addresses.Count == 0)
        {
            return UrlSafetyResult.Rejected(UrlRejectionReason.DnsResolutionFailed);
        }

        return !policy.AllowNonPublicAddresses && addresses.Any(static address => !IsPublicInternetAddress(address))
            ? UrlSafetyResult.Rejected(UrlRejectionReason.NonPublicAddress)
            : UrlSafetyResult.Allowed;
    }

    /// <summary>Returns a canonical HTTP(S) URL without a fragment for stable comparison and deduplication.</summary>
    /// <param name="url">An absolute HTTP(S) URL.</param>
    /// <returns>The canonical URL.</returns>
    public static Uri Canonicalize(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("The URL must be absolute.", nameof(url));
        }

        var builder = new UriBuilder(url) { Fragment = string.Empty };
        if ((builder.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && builder.Port == 80)
            || (builder.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    private static bool IsLocalHostName(string hostName) =>
        hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || hostName.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicInternetAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPublicIpv4(bytes);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // fc00::/7 unique-local, fe80::/10 link-local, ff00::/8 multicast and 2001:db8::/32 documentation.
        return (bytes[0] & 0xFE) != 0xFC
            && !(bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            && bytes[0] != 0xFF
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }

    private static bool IsPublicIpv4(byte[] bytes)
    {
        var first = bytes[0];
        var second = bytes[1];

        return first is not 0 and not 10 and not 127
            && !(first == 100 && second is >= 64 and <= 127)
            && !(first == 169 && second == 254)
            && !(first == 172 && second is >= 16 and <= 31)
            && !(first == 192 && second == 0 && bytes[2] is 0 or 2)
            && !(first == 192 && second == 88 && bytes[2] == 99)
            && !(first == 192 && second == 168)
            && !(first == 198 && second is 18 or 19)
            && !(first == 198 && second == 51 && bytes[2] == 100)
            && !(first == 203 && second == 0 && bytes[2] == 113)
            && first < 224;
    }
}
