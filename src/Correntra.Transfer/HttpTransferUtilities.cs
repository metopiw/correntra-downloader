using System.Net;
using System.Net.Http.Headers;

namespace Correntra.Transfer;

internal static class HttpTransferUtilities
{
    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Host",
        "If-Range",
        "Range",
    };

    public static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri source,
        IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(method, source);
        request.Headers.UserAgent.ParseAdd("Correntra/0.1");
        request.Headers.AcceptEncoding.ParseAdd("identity");

        foreach (var (name, value) in headers)
        {
            if (ManagedHeaders.Contains(name))
            {
                continue;
            }

            if (name.Contains('\r') ||
                name.Contains('\n') ||
                value.Contains('\r') ||
                value.Contains('\n'))
            {
                throw new ArgumentException("HTTP header names and values cannot contain line breaks.", nameof(headers));
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Dispose();
                throw new ArgumentException($"The HTTP header '{name}' is not valid for a download request.", nameof(headers));
            }
        }

        return request;
    }

    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        RetryOptions options,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
    {
        ValidateRetryOptions(options);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
                if (!IsRetryable(response.StatusCode) || attempt == options.MaxAttempts)
                {
                    return response;
                }

                var serverDelay = GetServerDelay(response);
                response.Dispose();
                await DelayBeforeRetryAsync(options, attempt, serverDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsTransient(exception, cancellationToken) && attempt < options.MaxAttempts)
            {
                lastException = exception;
                await DelayBeforeRetryAsync(options, attempt, null, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TransferException("The HTTP operation exhausted its retry attempts.", lastException!);
    }

    public static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or IOException or EndOfStreamException or TaskCanceledException or TimeoutException;

    public static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    public static async Task DelayBeforeRetryAsync(
        RetryOptions options,
        int attempt,
        TimeSpan? serverDelay,
        CancellationToken cancellationToken)
    {
        var exponentialMilliseconds = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var cappedMilliseconds = Math.Min(exponentialMilliseconds, options.MaximumDelay.TotalMilliseconds);
        var jitter = 1 + ((Random.Shared.NextDouble() * 2 - 1) * options.JitterFactor);
        var delay = serverDelay ?? TimeSpan.FromMilliseconds(Math.Max(0, cappedMilliseconds * jitter));
        if (delay > options.MaximumDelay)
        {
            delay = options.MaximumDelay;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public static void ValidateRetryOptions(RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumDelay, options.BaseDelay);
        if (options.JitterFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Jitter must be between zero and one.");
        }
    }

    private static TimeSpan? GetServerDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }
}
