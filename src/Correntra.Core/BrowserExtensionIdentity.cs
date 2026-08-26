namespace Correntra.Core;

/// <summary>
/// Canonical browser extension identity, shared by every component that has
/// to agree on it: the agent's loopback HTTP bridge (Origin pinning), the
/// native messaging registrar and validator, and the packaged extension
/// itself. The manifest carries a fixed <c>key</c> (SPKI, base64), so Chrome
/// derives this exact ID on every machine instead of hashing the unpacked
/// folder path.
/// </summary>
public static class BrowserExtensionIdentity
{
    public const string ExtensionId = "bhnibkknmmodoehpaeoijnkabfdmbdjp";

    /// <summary>The Origin header value sent by the extension's service worker.</summary>
    public const string ExtensionOrigin = "chrome-extension://" + ExtensionId + "/";

    /// <summary>Fixed public key embedded in the extension manifest.</summary>
    public const string ManifestKeyBase64 = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAt92ToEo5DxMfwI6xM/tp2tVgamL2fZ8U861wNQmtxV7ztaliv29jwg+qwQB9dqzHdvAqqrEm7mcvuQL6UE5Bha1VBN7CWZLMsLG/ptu772AQMH5r7pJUAaLr8RV4RFdLeraWbIEUVvDDQ/xuTCY97JBCZQ6IZ/nBKxbGuk8YLrQGIb40GekOoyXDc/zv2vZKdB9FAyXY9f+rzOO2L2ciyPXE/jTLrT6qiorv7KmQEtexGGR7/jNd4N1ND/G+jZ03YEb0hdfNV76rb9Lu9uAkQ9CrBwxQu5EzxC2UpEVNHM84wS8GqJ/UTtqSouOKaoHJ6esfQUGu0cGuXPhQhwBOIwIDAQAB";
}
