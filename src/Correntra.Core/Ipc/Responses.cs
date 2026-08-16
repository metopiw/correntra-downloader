using Correntra.Core.Browser;
using Correntra.Core.Internal;

namespace Correntra.Core.Ipc;

public sealed record PingResponse : IIpcResponse
{
    public PingResponse(string agentVersion)
    {
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), 100);
    }

    public string Type => "ping.response";

    public string AgentVersion { get; }
}

public sealed record CommandAcceptedResponse : IIpcResponse
{
    public CommandAcceptedResponse(JobId? jobId = null)
    {
        if (jobId is { IsEmpty: true })
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(jobId));
        }

        JobId = jobId;
    }

    public string Type => "command.accepted";

    public JobId? JobId { get; }
}

public sealed record CommandRejectedResponse : IIpcResponse
{
    public CommandRejectedResponse(string errorCode, string userMessage, bool isRetryable = false)
    {
        ErrorCode = Guard.NotNullOrWhiteSpace(errorCode, nameof(errorCode), 80);
        UserMessage = Guard.NotNullOrWhiteSpace(userMessage, nameof(userMessage), 2_000);
        IsRetryable = isRetryable;
    }

    public string Type => "command.rejected";

    public string ErrorCode { get; }

    public string UserMessage { get; }

    public bool IsRetryable { get; }
}

public sealed record BrowserCaptureResponse : IIpcResponse
{
    public BrowserCaptureResponse(BrowserCaptureResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public string Type => "browser.download.capture.response";

    public BrowserCaptureResult Result { get; }
}
