using System.Security.Cryptography;
using System.Text;

namespace Correntra.Platform.Windows.Security;

public static class WindowsDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Correntra.Downloader.Credentials.v1");

    public static byte[] Protect(ReadOnlySpan<byte> clearText)
    {
        EnsureWindows();
        return ProtectedData.Protect(clearText.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    public static byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        EnsureWindows();
        return ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is available only on Windows.");
        }
    }
}
