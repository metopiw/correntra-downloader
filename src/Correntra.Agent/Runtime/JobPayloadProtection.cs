using Correntra.Platform.Windows.Security;

namespace Correntra.Agent.Runtime;

public interface IJobPayloadProtector
{
    byte[] Protect(ReadOnlySpan<byte> payload);

    byte[] Unprotect(ReadOnlySpan<byte> protectedPayload);
}

public sealed class WindowsJobPayloadProtector : IJobPayloadProtector
{
    public byte[] Protect(ReadOnlySpan<byte> payload) => WindowsDataProtector.Protect(payload);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload) => WindowsDataProtector.Unprotect(protectedPayload);
}

