using Correntra.Media.Models;

namespace Correntra.Media.Resolution;

public interface IMediaResolver
{
    Task<MediaDescriptor> ResolveAsync(MediaCandidate candidate, CancellationToken cancellationToken = default);
}

public class MediaResolutionException : Exception
{
    public MediaResolutionException(string message)
        : base(message)
    {
    }

    public MediaResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DrmProtectedMediaException : MediaResolutionException
{
    public DrmProtectedMediaException(string message)
        : base(message)
    {
    }
}
