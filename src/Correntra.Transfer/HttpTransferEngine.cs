using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Correntra.Transfer;

/// <summary>
/// A UI-independent HTTP/HTTPS transfer engine with segmented downloads and durable resume data.
/// </summary>
public sealed class HttpTransferEngine : IDisposable
{
    private const int BufferSize = 256 * 1024;

    // A healthy connection must produce bytes within this window; otherwise the
    // read is abandoned so the segment's retry loop can reconnect instead of
    // hanging forever on a stalled server.
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient client;
    private readonly HttpResourceProbe probe;
    private readonly IBandwidthLimiter globalBandwidthLimiter;
    private readonly ITransferCheckpointStore checkpointStore;
    private readonly bool ownsClient;
    private readonly bool ownsCheckpointStore;
    private bool disposed;

    public HttpTransferEngine(
        HttpClient? httpClient = null,
        IBandwidthLimiter? globalBandwidthLimiter = null,
        ITransferCheckpointStore? checkpointStore = null)
    {
        if (httpClient is null)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 20,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                // Segmented downloads open several connections per server; HTTP/2
                // multiplexing needs parallel streams to reach full throughput.
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            client = new HttpClient(handler, true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            ownsClient = true;
        }
        else
        {
            client = httpClient;
        }

        this.globalBandwidthLimiter = globalBandwidthLimiter ?? UnlimitedBandwidthLimiter.Instance;
        if (checkpointStore is null)
        {
            this.checkpointStore = new JsonTransferCheckpointStore();
            ownsCheckpointStore = true;
        }
        else
        {
            this.checkpointStore = checkpointStore;
        }

        probe = new HttpResourceProbe(client);
    }

    public Task<RemoteResourceInfo> ProbeAsync(
        Uri source,
        IReadOnlyDictionary<string, string>? headers = null,
        RetryOptions? retry = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return probe.ProbeAsync(source, headers, retry, default, cancellationToken);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateRequest(request);

        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var temporaryPath = GetTemporaryPath(destinationPath);
        var checkpointPath = GetCheckpointPath(destinationPath);
        if (!request.Overwrite && File.Exists(destinationPath))
        {
            throw new IOException($"The destination file already exists: {destinationPath}");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tracker = new ProgressTracker(request.Progress);
        tracker.Report(TransferPhase.Probing, null, false, true);
        // A server that accepts the connection but never answers would otherwise
        // park the job at 0% indefinitely; cap the probe and let the job-level
        // retry loop take over.
        using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeBudget.CancelAfter(ProbeTimeout);
        // Probing must fail fast; the heavy retry budget belongs to the actual
        // transfer and the job-level automatic retries.
        var probeRetry = request.Retry with { MaxAttempts = Math.Min(request.Retry.MaxAttempts, 3) };
        var resource = await probe.ProbeAsync(
            request.Source,
            request.Headers,
            probeRetry,
            request.PauseToken,
            probeBudget.Token).ConfigureAwait(false);

        tracker.SetTotal(resource.ContentLength);
        var ranges = CreateRanges(resource, request);
        var checkpoint = await checkpointStore.LoadAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        var canResume = CanResume(checkpoint, resource, request.Source, temporaryPath);
        var segments = canResume
            ? checkpoint!.Segments.Select(SegmentRuntime.FromCheckpoint).ToArray()
            : ranges.Select(range => new SegmentRuntime(range.Start, range.EndInclusive, 0)).ToArray();

        if (!canResume)
        {
            await checkpointStore.DeleteAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
            File.Delete(temporaryPath);
        }

        var resumedBytes = segments.Sum(segment => segment.CompletedBytes);
        tracker.SetTransferred(resumedBytes);
        var wasResumed = resumedBytes > 0;
        using var checkpointSession = new CheckpointSession(
            checkpointStore,
            checkpointPath,
            request.Source,
            resource,
            segments);

        if (resource.ContentLength == 0)
        {
            await File.WriteAllBytesAsync(temporaryPath, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var handle = File.OpenHandle(
                temporaryPath,
                canResume ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            if (resource.ContentLength is { } contentLength)
            {
                RandomAccess.SetLength(handle, contentLength);
            }

            await checkpointSession.SaveAsync(true, CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (resource.SupportsRanges && resource.ContentLength is not null)
                {
                    await DownloadRangesAsync(
                        request,
                        resource,
                        handle,
                        segments,
                        checkpointSession,
                        tracker,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DownloadSequentiallyAsync(
                        request,
                        resource,
                        handle,
                        segments[0],
                        checkpointSession,
                        tracker,
                        cancellationToken).ConfigureAwait(false);
                }

                RandomAccess.FlushToDisk(handle);
            }
            catch (OperationCanceledException)
            {
                await checkpointSession.SaveAsync(true, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (RemoteResourceChangedException)
            {
                handle.Dispose();
                await DeletePartialStateAsync(temporaryPath, checkpointPath).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await checkpointSession.SaveAsync(true, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        if (resource.ContentLength is { } expectedLength)
        {
            var actualLength = new FileInfo(temporaryPath).Length;
            if (actualLength != expectedLength)
            {
                throw new TransferException(
                    $"The completed file length was {actualLength} bytes; {expectedLength} bytes were expected.");
            }
        }

        tracker.Report(TransferPhase.Verifying, resource.ContentLength, false, true);
        string? verifiedHash = null;
        if (request.ExpectedHash is { } requirement)
        {
            var actualHash = await ComputeHashAsync(
                temporaryPath,
                requirement.Algorithm,
                cancellationToken).ConfigureAwait(false);
            var expectedHash = ParseExpectedHash(requirement);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                verifiedHash = Convert.ToHexString(actualHash);
                await DeletePartialStateAsync(temporaryPath, checkpointPath).ConfigureAwait(false);
                throw new HashMismatchException(Convert.ToHexString(expectedHash), verifiedHash);
            }

            verifiedHash = Convert.ToHexString(actualHash);
        }

        tracker.Report(TransferPhase.Finalizing, resource.ContentLength, false, true);
        File.Move(temporaryPath, destinationPath, request.Overwrite);
        await checkpointStore.DeleteAsync(checkpointPath, CancellationToken.None).ConfigureAwait(false);
        var finalLength = new FileInfo(destinationPath).Length;
        tracker.SetTransferred(finalLength);
        tracker.Report(TransferPhase.Completed, finalLength, false, true);

        return new DownloadResult(
            resource.FinalUri,
            destinationPath,
            finalLength,
            verifiedHash,
            wasResumed,
            tracker.Elapsed);
    }

    public static string GetTemporaryPath(string destinationPath) =>
        Path.GetFullPath(destinationPath) + ".correntra.part";

    public static string GetCheckpointPath(string destinationPath) =>
        GetTemporaryPath(destinationPath) + ".checkpoint.json";

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsClient)
        {
            client.Dispose();
        }

        if (ownsCheckpointStore && checkpointStore is IDisposable disposableStore)
        {
            disposableStore.Dispose();
        }
    }

    private static IReadOnlyList<ByteRange> CreateRanges(RemoteResourceInfo resource, DownloadRequest request)
    {
        if (resource.ContentLength is null)
        {
            return new[] { new ByteRange(0, -1) };
        }

        if (!resource.SupportsRanges)
        {
            return resource.ContentLength == 0
                ? Array.Empty<ByteRange>()
                : new[] { new ByteRange(0, resource.ContentLength.Value - 1) };
        }

        return SegmentPlanner.Plan(
            resource.ContentLength.Value,
            request.MaxSegments,
            request.MinimumSegmentSizeBytes);
    }

    private static bool CanResume(
        TransferCheckpoint? checkpoint,
        RemoteResourceInfo resource,
        Uri requestedUri,
        string temporaryPath)
    {
        if (checkpoint is null ||
            checkpoint.FormatVersion != 1 ||
            !File.Exists(temporaryPath) ||
            !string.Equals(checkpoint.Source, requestedUri.AbsoluteUri, StringComparison.Ordinal) ||
            checkpoint.ContentLength != resource.ContentLength ||
            resource.ContentLength is null ||
            !resource.SupportsRanges ||
            checkpoint.Segments is null ||
            checkpoint.Segments.Count == 0)
        {
            return false;
        }

        var hasMatchingValidator = HasMatchingValidator(checkpoint, resource);
        if (!hasMatchingValidator || new FileInfo(temporaryPath).Length != resource.ContentLength.Value)
        {
            return false;
        }

        long cursor = 0;
        foreach (var segment in checkpoint.Segments)
        {
            if (segment.Start != cursor ||
                segment.EndInclusive < segment.Start ||
                segment.CompletedBytes < 0 ||
                segment.CompletedBytes > segment.EndInclusive - segment.Start + 1)
            {
                return false;
            }

            cursor = segment.EndInclusive + 1;
        }

        return cursor == resource.ContentLength.Value;
    }

    private static bool HasMatchingValidator(TransferCheckpoint checkpoint, RemoteResourceInfo resource)
    {
        if (IsStrongEntityTag(resource.EntityTag) && IsStrongEntityTag(checkpoint.EntityTag))
        {
            return string.Equals(checkpoint.EntityTag, resource.EntityTag, StringComparison.Ordinal);
        }

        return resource.LastModified is not null && checkpoint.LastModified == resource.LastModified;
    }

    private async Task DownloadRangesAsync(
        DownloadRequest request,
        RemoteResourceInfo resource,
        SafeFileHandle handle,
        IReadOnlyList<SegmentRuntime> segments,
        CheckpointSession checkpointSession,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        // Give ranges several collective rounds. A transient network drop that
        // defeats one segment's internal retries must be retried against the
        // still-incomplete ranges instead of failing the whole transfer.
        for (var round = 1; round <= request.Retry.MaxAttempts; round++)
        {
            var pending = segments.Where(segment => segment.RemainingBytes > 0).ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            Exception? firstFailure = null;
            var tasks = pending.Select(async segment =>
            {
                try
                {
                    await DownloadRangeAsync(
                        request,
                        resource,
                        handle,
                        segment,
                        checkpointSession,
                        tracker,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    HttpTransferUtilities.IsTransient(exception, cancellationToken))
                {
                    // A single segment failing must not cancel the healthy
                    // siblings; the remaining ranges are retried as a group in
                    // the next round.
                    Interlocked.CompareExchange(ref firstFailure, exception, null);
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (segments.All(segment => segment.RemainingBytes == 0))
            {
                return;
            }

            if (firstFailure is not null && round < request.Retry.MaxAttempts)
            {
                await checkpointSession.SaveAsync(true, CancellationToken.None).ConfigureAwait(false);
                await HttpTransferUtilities.DelayBeforeRetryAsync(
                    request.Retry,
                    round,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (segments.Any(segment => segment.RemainingBytes > 0))
        {
            throw new TransferException("One or more byte ranges could not be completed.");
        }
    }

    private async Task DownloadRangeAsync(
        DownloadRequest request,
        RemoteResourceInfo resource,
        SafeFileHandle handle,
        SegmentRuntime segment,
        CheckpointSession checkpointSession,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.ChangeActiveSegments(1);
        try
        {
            for (var attempt = 1; attempt <= request.Retry.MaxAttempts; attempt++)
            {
                await WaitForResumeAsync(request.PauseToken, tracker, resource.ContentLength, cancellationToken)
                    .ConfigureAwait(false);
                var requestStart = segment.Start + segment.CompletedBytes;
                if (requestStart > segment.EndInclusive)
                {
                    return;
                }

                try
                {
                    using var message = CreateRangeRequest(request, resource, requestStart, segment.EndInclusive);
                    using var response = await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                        .WaitAsync(ReadStallTimeout, cancellationToken).ConfigureAwait(false);

                    if (HttpTransferUtilities.IsRetryable(response.StatusCode))
                    {
                        if (attempt == request.Retry.MaxAttempts)
                        {
                            response.EnsureSuccessStatusCode();
                        }

                        await HttpTransferUtilities.DelayBeforeRetryAsync(
                            request.Retry,
                            attempt,
                            null,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    ValidateRangeResponse(response, resource, requestStart, segment.EndInclusive);
                    await CopyRangeResponseAsync(
                        response,
                        request,
                        resource.ContentLength,
                        handle,
                        segment,
                        checkpointSession,
                        tracker,
                        cancellationToken).ConfigureAwait(false);

                    if (segment.RemainingBytes == 0)
                    {
                        return;
                    }
                }
                catch (Exception exception) when (
                    HttpTransferUtilities.IsTransient(exception, cancellationToken) &&
                    attempt < request.Retry.MaxAttempts)
                {
                    await checkpointSession.SaveAsync(true, CancellationToken.None).ConfigureAwait(false);
                    await HttpTransferUtilities.DelayBeforeRetryAsync(
                        request.Retry,
                        attempt,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            throw new TransferException("A byte range exhausted its retry attempts.");
        }
        finally
        {
            tracker.ChangeActiveSegments(-1);
        }
    }

    private async Task CopyRangeResponseAsync(
        HttpResponseMessage response,
        DownloadRequest request,
        long? totalLength,
        SafeFileHandle handle,
        SegmentRuntime segment,
        CheckpointSession checkpointSession,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (segment.RemainingBytes > 0)
            {
                await WaitForResumeAsync(request.PauseToken, tracker, totalLength, cancellationToken)
                    .ConfigureAwait(false);
                var requested = (int)Math.Min(buffer.Length, segment.RemainingBytes);
                var throttled = await ApplyBandwidthLimitsAsync(
                    requested,
                    request.BandwidthLimiter,
                    cancellationToken).ConfigureAwait(false);
                if (throttled)
                {
                    tracker.Report(TransferPhase.Throttled, totalLength, true, false);
                }

                var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .AsTask().WaitAsync(ReadStallTimeout, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The server closed a ranged response before all bytes arrived.");
                }

                var writeOffset = segment.Start + segment.CompletedBytes;
                await RandomAccess.WriteAsync(
                    handle,
                    buffer.AsMemory(0, read),
                    writeOffset,
                    cancellationToken).ConfigureAwait(false);
                segment.AddCompleted(read);
                tracker.AddTransferred(read);
                tracker.Report(TransferPhase.Downloading, totalLength, throttled, false);
                await checkpointSession.SaveAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DownloadSequentiallyAsync(
        DownloadRequest request,
        RemoteResourceInfo resource,
        SafeFileHandle handle,
        SegmentRuntime segment,
        CheckpointSession checkpointSession,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.ChangeActiveSegments(1);
        try
        {
            for (var attempt = 1; attempt <= request.Retry.MaxAttempts; attempt++)
            {
                if (segment.CompletedBytes > 0)
                {
                    tracker.AddTransferred(-segment.Reset());
                    RandomAccess.SetLength(handle, resource.ContentLength ?? 0);
                }

                await WaitForResumeAsync(request.PauseToken, tracker, resource.ContentLength, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    using var message = HttpTransferUtilities.CreateRequest(
                        HttpMethod.Get,
                        request.Source,
                        request.Headers);
                    using var response = await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                        .WaitAsync(ReadStallTimeout, cancellationToken).ConfigureAwait(false);

                    if (HttpTransferUtilities.IsRetryable(response.StatusCode))
                    {
                        if (attempt == request.Retry.MaxAttempts)
                        {
                            response.EnsureSuccessStatusCode();
                        }

                        await HttpTransferUtilities.DelayBeforeRetryAsync(
                            request.Retry,
                            attempt,
                            null,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    ValidateResponseValidator(response, resource);
                    await CopySequentialResponseAsync(
                        response,
                        request,
                        resource.ContentLength,
                        handle,
                        segment,
                        checkpointSession,
                        tracker,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (
                    HttpTransferUtilities.IsTransient(exception, cancellationToken) &&
                    attempt < request.Retry.MaxAttempts)
                {
                    await checkpointSession.SaveAsync(true, CancellationToken.None).ConfigureAwait(false);
                    await HttpTransferUtilities.DelayBeforeRetryAsync(
                        request.Retry,
                        attempt,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            throw new TransferException("The sequential transfer exhausted its retry attempts.");
        }
        finally
        {
            tracker.ChangeActiveSegments(-1);
        }
    }

    private async Task CopySequentialResponseAsync(
        HttpResponseMessage response,
        DownloadRequest request,
        long? expectedLength,
        SafeFileHandle handle,
        SegmentRuntime segment,
        CheckpointSession checkpointSession,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                await WaitForResumeAsync(request.PauseToken, tracker, expectedLength, cancellationToken)
                    .ConfigureAwait(false);
                var remaining = expectedLength is null ? buffer.Length : expectedLength.Value - segment.CompletedBytes;
                if (remaining == 0)
                {
                    break;
                }

                var requested = (int)Math.Min(buffer.Length, remaining);
                var throttled = await ApplyBandwidthLimitsAsync(
                    requested,
                    request.BandwidthLimiter,
                    cancellationToken).ConfigureAwait(false);
                if (throttled)
                {
                    tracker.Report(TransferPhase.Throttled, expectedLength, true, false);
                }

                var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .AsTask().WaitAsync(ReadStallTimeout, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(
                    handle,
                    buffer.AsMemory(0, read),
                    segment.CompletedBytes,
                    cancellationToken).ConfigureAwait(false);
                segment.AddCompleted(read);
                tracker.AddTransferred(read);
                tracker.Report(TransferPhase.Downloading, expectedLength, throttled, false);
                await checkpointSession.SaveAsync(false, cancellationToken).ConfigureAwait(false);
            }

            if (expectedLength is { } knownLength && segment.CompletedBytes != knownLength)
            {
                throw new EndOfStreamException(
                    $"The server closed the response after {segment.CompletedBytes} of {knownLength} bytes.");
            }

            if (expectedLength is null)
            {
                RandomAccess.SetLength(handle, segment.CompletedBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<bool> ApplyBandwidthLimitsAsync(
        int byteCount,
        IBandwidthLimiter? jobLimiter,
        CancellationToken cancellationToken)
    {
        var globalLease = await globalBandwidthLimiter.AcquireAsync(byteCount, cancellationToken).ConfigureAwait(false);
        var jobLease = jobLimiter is null
            ? new BandwidthLease(TimeSpan.Zero)
            : await jobLimiter.AcquireAsync(byteCount, cancellationToken).ConfigureAwait(false);
        return globalLease.WasThrottled || jobLease.WasThrottled;
    }

    private static async Task WaitForResumeAsync(
        PauseToken pauseToken,
        ProgressTracker tracker,
        long? totalLength,
        CancellationToken cancellationToken)
    {
        if (pauseToken.IsPaused)
        {
            tracker.Report(TransferPhase.Paused, totalLength, false, true);
            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage CreateRangeRequest(
        DownloadRequest request,
        RemoteResourceInfo resource,
        long start,
        long endInclusive)
    {
        var message = HttpTransferUtilities.CreateRequest(HttpMethod.Get, request.Source, request.Headers);
        message.Headers.Range = new RangeHeaderValue(start, endInclusive);
        if (IsStrongEntityTag(resource.EntityTag))
        {
            message.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(resource.EntityTag!));
        }
        else if (resource.LastModified is { } lastModified)
        {
            message.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
        }

        return message;
    }

    private static void ValidateRangeResponse(
        HttpResponseMessage response,
        RemoteResourceInfo resource,
        long requestedStart,
        long requestedEnd)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (IsStrongEntityTag(resource.EntityTag) || resource.LastModified is not null)
            {
                throw new RemoteResourceChangedException(
                    "The server stopped honoring If-Range; the remote resource may have changed.");
            }

            throw new InvalidRangeResponseException("The server ignored a validated byte-range request.");
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidRangeResponseException(
                $"Expected HTTP 206 for a byte range, received {(int)response.StatusCode}.");
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.Unit != "bytes" ||
            contentRange.From != requestedStart ||
            contentRange.To != requestedEnd ||
            contentRange.Length != resource.ContentLength)
        {
            throw new InvalidRangeResponseException("The Content-Range header did not match the requested byte range.");
        }

        var expectedContentLength = requestedEnd - requestedStart + 1;
        if (response.Content.Headers.ContentLength is { } responseLength &&
            responseLength != expectedContentLength)
        {
            throw new InvalidRangeResponseException("The ranged response body length was inconsistent with Content-Range.");
        }

        ValidateResponseValidator(response, resource);
    }

    private static void ValidateResponseValidator(HttpResponseMessage response, RemoteResourceInfo resource)
    {
        var responseEntityTag = response.Headers.ETag?.ToString();
        if (IsStrongEntityTag(resource.EntityTag) &&
            responseEntityTag is not null &&
            !string.Equals(responseEntityTag, resource.EntityTag, StringComparison.Ordinal))
        {
            throw new RemoteResourceChangedException("The server entity tag changed during the transfer.");
        }

        var responseLastModified = response.Content.Headers.LastModified;
        if (!IsStrongEntityTag(resource.EntityTag) &&
            resource.LastModified is not null &&
            responseLastModified is not null &&
            responseLastModified != resource.LastModified)
        {
            throw new RemoteResourceChangedException("The remote resource modification date changed during the transfer.");
        }
    }

    private static bool IsStrongEntityTag(string? entityTag) =>
        !string.IsNullOrWhiteSpace(entityTag) &&
        !entityTag.StartsWith("W/", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Source.IsAbsoluteUri ||
            (request.Source.Scheme != Uri.UriSchemeHttp && request.Source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS addresses are supported.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaxSegments, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MinimumSegmentSizeBytes, 1);
        ArgumentNullException.ThrowIfNull(request.Headers);
        ArgumentNullException.ThrowIfNull(request.Retry);
        HttpTransferUtilities.ValidateRetryOptions(request.Retry);
        if (request.ExpectedHash is { } requirement)
        {
            _ = ParseExpectedHash(requirement);
        }
    }

    private static byte[] ParseExpectedHash(HashRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(requirement.HexDigest.Replace("-", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The expected hash must be hexadecimal.", nameof(requirement), exception);
        }

        var expectedLength = requirement.Algorithm switch
        {
            TransferHashAlgorithm.Sha256 => 32,
            TransferHashAlgorithm.Sha384 => 48,
            TransferHashAlgorithm.Sha512 => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement)),
        };
        if (bytes.Length != expectedLength)
        {
            throw new ArgumentException(
                $"A {requirement.Algorithm} digest must contain {expectedLength} bytes.",
                nameof(requirement));
        }

        return bytes;
    }

    private static async Task<byte[]> ComputeHashAsync(
        string path,
        TransferHashAlgorithm algorithm,
        CancellationToken cancellationToken)
    {
        using var incrementalHash = IncrementalHash.CreateHash(algorithm switch
        {
            TransferHashAlgorithm.Sha256 => HashAlgorithmName.SHA256,
            TransferHashAlgorithm.Sha384 => HashAlgorithmName.SHA384,
            TransferHashAlgorithm.Sha512 => HashAlgorithmName.SHA512,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        });
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                incrementalHash.AppendData(buffer, 0, read);
            }

            return incrementalHash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask DeletePartialStateAsync(string temporaryPath, string checkpointPath)
    {
        File.Delete(temporaryPath);
        await checkpointStore.DeleteAsync(checkpointPath, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class SegmentRuntime
    {
        private long completedBytes;

        public SegmentRuntime(long start, long endInclusive, long completedBytes)
        {
            Start = start;
            EndInclusive = endInclusive;
            this.completedBytes = completedBytes;
        }

        public long Start { get; }

        public long EndInclusive { get; }

        public long CompletedBytes => Interlocked.Read(ref completedBytes);

        public long RemainingBytes => EndInclusive < Start
            ? long.MaxValue
            : EndInclusive - Start + 1 - CompletedBytes;

        public static SegmentRuntime FromCheckpoint(SegmentCheckpoint checkpoint) =>
            new(checkpoint.Start, checkpoint.EndInclusive, checkpoint.CompletedBytes);

        public void AddCompleted(long count) => Interlocked.Add(ref completedBytes, count);

        public long Reset() => Interlocked.Exchange(ref completedBytes, 0);

        public SegmentCheckpoint Snapshot() => new(Start, EndInclusive, CompletedBytes);
    }

    private sealed class CheckpointSession : IDisposable
    {
        private const long SaveIntervalBytes = 256 * 1024;
        private readonly ITransferCheckpointStore store;
        private readonly string path;
        private readonly Uri source;
        private readonly RemoteResourceInfo resource;
        private readonly IReadOnlyList<SegmentRuntime> segments;
        private readonly SemaphoreSlim saveGate = new(1, 1);
        private long lastSavedBytes;
        private long lastSavedTimestamp = Stopwatch.GetTimestamp();

        public CheckpointSession(
            ITransferCheckpointStore store,
            string path,
            Uri source,
            RemoteResourceInfo resource,
            IReadOnlyList<SegmentRuntime> segments)
        {
            this.store = store;
            this.path = path;
            this.source = source;
            this.resource = resource;
            this.segments = segments;
        }

        public async ValueTask SaveAsync(bool force, CancellationToken cancellationToken)
        {
            var currentBytes = segments.Sum(segment => segment.CompletedBytes);
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastSavedTimestamp));
            if (!force && currentBytes - Interlocked.Read(ref lastSavedBytes) < SaveIntervalBytes &&
                elapsed < TimeSpan.FromSeconds(1))
            {
                return;
            }

            if (!force && !await saveGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (force)
            {
                await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var snapshot = new TransferCheckpoint(
                    1,
                    source.AbsoluteUri,
                    resource.FinalUri.AbsoluteUri,
                    resource.ContentLength,
                    resource.EntityTag,
                    resource.LastModified,
                    segments.Select(segment => segment.Snapshot()).ToArray(),
                    DateTimeOffset.UtcNow);
                await store.SaveAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref lastSavedBytes, currentBytes);
                Interlocked.Exchange(ref lastSavedTimestamp, Stopwatch.GetTimestamp());
            }
            finally
            {
                saveGate.Release();
            }
        }

        public void Dispose() => saveGate.Dispose();
    }

    private sealed class ProgressTracker
    {
        private readonly IProgress<TransferProgress>? progress;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long bytesTransferred;
        private long totalBytes = -1;
        private int activeSegments;
        private long lastReportTimestamp;

        public ProgressTracker(IProgress<TransferProgress>? progress) => this.progress = progress;

        public TimeSpan Elapsed => stopwatch.Elapsed;

        public void SetTotal(long? value) => Interlocked.Exchange(ref totalBytes, value ?? -1);

        public void SetTransferred(long value) => Interlocked.Exchange(ref bytesTransferred, value);

        public void AddTransferred(long value) => Interlocked.Add(ref bytesTransferred, value);

        public void ChangeActiveSegments(int delta) => Interlocked.Add(ref activeSegments, delta);

        public void Report(TransferPhase phase, long? total, bool throttled, bool force)
        {
            if (progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref lastReportTimestamp);
            if (!force && previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            if (!force && Interlocked.CompareExchange(ref lastReportTimestamp, now, previous) != previous)
            {
                return;
            }

            if (force)
            {
                Interlocked.Exchange(ref lastReportTimestamp, now);
            }

            var transferred = Interlocked.Read(ref bytesTransferred);
            var knownTotal = total ?? (Interlocked.Read(ref totalBytes) is var stored && stored >= 0 ? stored : null);
            var seconds = stopwatch.Elapsed.TotalSeconds;
            var speed = seconds > 0 ? transferred / seconds : 0;
            TimeSpan? remaining = knownTotal is { } length && speed > 0
                ? TimeSpan.FromSeconds(Math.Max(0, (length - transferred) / speed))
                : null;

            try
            {
                progress.Report(new TransferProgress(
                    phase,
                    transferred,
                    knownTotal,
                    speed,
                    remaining,
                    Volatile.Read(ref activeSegments),
                    throttled));
            }
            catch (Exception)
            {
                // Consumer progress callbacks cannot compromise the transfer itself.
            }
        }
    }
}
