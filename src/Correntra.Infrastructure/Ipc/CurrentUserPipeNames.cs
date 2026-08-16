using System.Security.Cryptography;
using System.Text;

namespace Correntra.Infrastructure.Ipc;

/// <summary>
/// Per-user named pipe naming shared by every Correntra process. Pipe names are
/// suffixed with a hash of the current user identity so multiple Windows users
/// on the same machine never talk to each other's instances.
/// </summary>
public static class CurrentUserPipeNames
{
    public static string For(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return $"Correntra.{service}.{CurrentUserHash()}.v1";
    }

    private static string CurrentUserHash()
    {
        string identity = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
