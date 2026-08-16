using Correntra.Core.Internal;

namespace Correntra.Core.Downloads;

public sealed record DownloadFailure
{
    public DownloadFailure(string code, string userMessage, bool isRetryable)
    {
        Code = Guard.NotNullOrWhiteSpace(code, nameof(code), 80);
        UserMessage = Guard.NotNullOrWhiteSpace(userMessage, nameof(userMessage), 2_000);
        IsRetryable = isRetryable;
    }

    public string Code { get; }

    public string UserMessage { get; }

    public bool IsRetryable { get; }
}
