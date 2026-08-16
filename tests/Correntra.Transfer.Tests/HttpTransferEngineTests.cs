using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable xUnit1030 // Library-style awaits are intentional in loopback integration tests.

namespace Correntra.Transfer.Tests;

public sealed class HttpTransferEngineTests
{
    [Fact]
    public async Task ProbeAsync_FollowsRedirectAndParsesExtendedFileName()
    {
        var content = CreateContent(8_192);
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            if (request.Target == "/start")
            {
                await LoopbackHttpServer.WriteResponseAsync(
                    stream,
                    302,
                    "Found",
                    new Dictionary<string, string> { ["Location"] = "/files/item" }).ConfigureAwait(false);
                return;
            }

            await ServeResourceAsync(request, stream, content, true, "attachment; filename*=UTF-8''m%C3%BCzik.mp3")
                .ConfigureAwait(false);
        });
        using var engine = new HttpTransferEngine();

        var result = await engine.ProbeAsync(new Uri(server.BaseUri, "start")).ConfigureAwait(false);

        Assert.Equal(new Uri(server.BaseUri, "files/item"), result.FinalUri);
        Assert.Equal(content.Length, result.ContentLength);
        Assert.True(result.SupportsRanges);
        Assert.Equal("müzik.mp3", result.SuggestedFileName);
        Assert.Equal("\"test-v1\"", result.EntityTag);
    }

    [Fact]
    public async Task DownloadAsync_UsesValidatedRangesAndVerifiesSha256()
    {
        var content = CreateContent(600_123);
        var observedRanges = new ConcurrentBag<(long Start, long End)>();
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            var range = LoopbackHttpServer.ParseRange(request);
            if (range is { } value)
            {
                observedRanges.Add(value);
            }

            await ServeResourceAsync(request, stream, content, true).ConfigureAwait(false);
        });
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "payload.bin");
        var expectedHash = Convert.ToHexString(SHA256.HashData(content));
        using var engine = new HttpTransferEngine();

        var result = await engine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "payload"), destination)
        {
            MaxSegments = 4,
            MinimumSegmentSizeBytes = 100_000,
            ExpectedHash = new HashRequirement(TransferHashAlgorithm.Sha256, expectedHash),
        }).ConfigureAwait(false);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination).ConfigureAwait(false));
        Assert.Equal(expectedHash, result.VerifiedHash);
        Assert.False(result.WasResumed);
        Assert.True(observedRanges.Count(range => range.End > 0) >= 4);
        Assert.False(File.Exists(HttpTransferEngine.GetCheckpointPath(destination)));
        Assert.False(File.Exists(HttpTransferEngine.GetTemporaryPath(destination)));
    }

    [Fact]
    public async Task DownloadAsync_FallsBackWhenServerIgnoresRanges()
    {
        var content = CreateContent(190_321);
        var directRequests = 0;
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            if (request.Method == "GET" && LoopbackHttpServer.ParseRange(request) is null)
            {
                Interlocked.Increment(ref directRequests);
            }

            await ServeResourceAsync(request, stream, content, false).ConfigureAwait(false);
        });
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "direct.bin");
        using var engine = new HttpTransferEngine();

        await engine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "direct"), destination))
            .ConfigureAwait(false);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination).ConfigureAwait(false));
        Assert.Equal(1, Volatile.Read(ref directRequests));
    }

    [Fact]
    public async Task DownloadAsync_RetriesAConnectionThatEndsEarly()
    {
        var content = CreateContent(220_000);
        var directAttempt = 0;
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            if (request.Method == "GET" && LoopbackHttpServer.ParseRange(request) is null &&
                Interlocked.Increment(ref directAttempt) == 1)
            {
                await LoopbackHttpServer.WriteResponseAsync(
                    stream,
                    200,
                    "OK",
                    new Dictionary<string, string>
                    {
                        ["Content-Length"] = content.Length.ToString(CultureInfo.InvariantCulture),
                        ["ETag"] = "\"test-v1\"",
                    },
                    content.AsMemory(0, content.Length / 3)).ConfigureAwait(false);
                return;
            }

            await ServeResourceAsync(request, stream, content, false).ConfigureAwait(false);
        });
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "retry.bin");
        using var engine = new HttpTransferEngine();

        await engine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "retry"), destination)
        {
            Retry = FastRetries(),
        }).ConfigureAwait(false);

        Assert.Equal(2, Volatile.Read(ref directAttempt));
        Assert.Equal(content, await File.ReadAllBytesAsync(destination).ConfigureAwait(false));
    }

    [Fact]
    public async Task DownloadAsync_ResumesFromDurableCheckpoint()
    {
        var content = CreateContent(2 * 1024 * 1024);
        var rangeStarts = new ConcurrentBag<long>();
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            var range = LoopbackHttpServer.ParseRange(request);
            if (request.Method == "HEAD")
            {
                await WriteHeadAsync(stream, content.Length, true).ConfigureAwait(false);
                return;
            }

            if (range is not { } requested)
            {
                await ServeResourceAsync(request, stream, content, true).ConfigureAwait(false);
                return;
            }

            rangeStarts.Add(requested.Start);
            var length = checked((int)(requested.End - requested.Start + 1));
            await LoopbackHttpServer.WriteResponseAsync(
                stream,
                206,
                "Partial Content",
                new Dictionary<string, string>
                {
                    ["Content-Length"] = length.ToString(CultureInfo.InvariantCulture),
                    ["Content-Range"] = $"bytes {requested.Start}-{requested.End}/{content.Length}",
                    ["Accept-Ranges"] = "bytes",
                    ["ETag"] = "\"test-v1\"",
                }).ConfigureAwait(false);

            const int chunk = 16 * 1024;
            for (var written = 0; written < length; written += chunk)
            {
                var count = Math.Min(chunk, length - written);
                await stream.WriteAsync(content.AsMemory(checked((int)requested.Start + written), count))
                    .ConfigureAwait(false);
                if (requested.End > 0)
                {
                    await Task.Delay(8).ConfigureAwait(false);
                }
            }
        });
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "resume.bin");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<TransferProgress>(value =>
        {
            if (value.Phase == TransferPhase.Downloading && value.BytesTransferred >= 256 * 1024)
            {
                cancellation.Cancel();
            }
        });

        using (var firstEngine = new HttpTransferEngine())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                firstEngine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "resume"), destination)
                {
                    MaxSegments = 1,
                    MinimumSegmentSizeBytes = 1,
                    Progress = progress,
                    Retry = FastRetries(),
                }, cancellation.Token)).ConfigureAwait(false);
        }

        Assert.True(File.Exists(HttpTransferEngine.GetCheckpointPath(destination)));
        Assert.True(File.Exists(HttpTransferEngine.GetTemporaryPath(destination)));
        using var secondEngine = new HttpTransferEngine();
        var result = await secondEngine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "resume"), destination)
        {
            MaxSegments = 1,
            MinimumSegmentSizeBytes = 1,
            Retry = FastRetries(),
        }).ConfigureAwait(false);

        Assert.True(result.WasResumed);
        Assert.Contains(rangeStarts, start => start > 0);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination).ConfigureAwait(false));
    }

    [Fact]
    public async Task DownloadAsync_HandlesChunkedResourceWithUnknownLength()
    {
        var content = CreateContent(73_001);
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            if (request.Method == "HEAD")
            {
                await LoopbackHttpServer.WriteResponseAsync(
                    stream,
                    200,
                    "OK",
                    new Dictionary<string, string> { ["ETag"] = "\"stream-v1\"" },
                    includeContentLength: false).ConfigureAwait(false);
                return;
            }

            await LoopbackHttpServer.WriteChunkedResponseAsync(stream, content).ConfigureAwait(false);
        });
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "stream.bin");
        using var engine = new HttpTransferEngine();

        var probe = await engine.ProbeAsync(new Uri(server.BaseUri, "stream")).ConfigureAwait(false);
        Assert.Null(probe.ContentLength);

        var result = await engine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "stream"), destination))
            .ConfigureAwait(false);

        Assert.Equal(content.Length, result.BytesTransferred);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination).ConfigureAwait(false));
    }

    [Fact]
    public async Task DownloadAsync_RejectsMismatchedContentRange()
    {
        var content = CreateContent(100_000);
        var rangedRequestCount = 0;
        await using var server = new LoopbackHttpServer(async (request, stream) =>
        {
            if (request.Method == "HEAD")
            {
                await WriteHeadAsync(stream, content.Length, true).ConfigureAwait(false);
                return;
            }

            var range = LoopbackHttpServer.ParseRange(request)!.Value;
            if (Interlocked.Increment(ref rangedRequestCount) == 1)
            {
                await ServeResourceAsync(request, stream, content, true).ConfigureAwait(false);
                return;
            }

            var length = checked((int)(range.End - range.Start + 1));
            await LoopbackHttpServer.WriteResponseAsync(
                stream,
                206,
                "Partial Content",
                new Dictionary<string, string>
                {
                    ["Content-Length"] = length.ToString(CultureInfo.InvariantCulture),
                    ["Content-Range"] = $"bytes 1-{range.End}/{content.Length}",
                    ["ETag"] = "\"test-v1\"",
                },
                content.AsMemory(0, length)).ConfigureAwait(false);
        });
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "bad-range.bin");
        using var engine = new HttpTransferEngine();

        await Assert.ThrowsAsync<InvalidRangeResponseException>(() =>
            engine.DownloadAsync(new DownloadRequest(new Uri(server.BaseUri, "bad-range"), destination)
            {
                MaxSegments = 1,
                MinimumSegmentSizeBytes = 1,
                Retry = FastRetries(),
            })).ConfigureAwait(false);
    }

    private static RetryOptions FastRetries() => new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaximumDelay = TimeSpan.FromMilliseconds(5),
        JitterFactor = 0,
    };

    private static byte[] CreateContent(int length)
    {
        var result = new byte[length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)((index * 31 + 17) % 251);
        }

        return result;
    }

    private static async Task ServeResourceAsync(
        LoopbackRequest request,
        Stream stream,
        byte[] content,
        bool supportsRanges,
        string? contentDisposition = null)
    {
        if (request.Method == "HEAD")
        {
            await WriteHeadAsync(stream, content.Length, supportsRanges, contentDisposition).ConfigureAwait(false);
            return;
        }

        var range = LoopbackHttpServer.ParseRange(request);
        if (supportsRanges && range is { } requested)
        {
            var length = checked((int)(requested.End - requested.Start + 1));
            await LoopbackHttpServer.WriteResponseAsync(
                (System.Net.Sockets.NetworkStream)stream,
                206,
                "Partial Content",
                CreateHeaders(content.Length, true, contentDisposition, requested.Start, requested.End),
                content.AsMemory(checked((int)requested.Start), length)).ConfigureAwait(false);
            return;
        }

        await LoopbackHttpServer.WriteResponseAsync(
            (System.Net.Sockets.NetworkStream)stream,
            200,
            "OK",
            CreateHeaders(content.Length, supportsRanges, contentDisposition),
            request.Method == "HEAD" ? ReadOnlyMemory<byte>.Empty : content).ConfigureAwait(false);
    }

    private static Task WriteHeadAsync(
        Stream stream,
        int length,
        bool supportsRanges,
        string? contentDisposition = null) =>
        LoopbackHttpServer.WriteResponseAsync(
            (System.Net.Sockets.NetworkStream)stream,
            200,
            "OK",
            CreateHeaders(length, supportsRanges, contentDisposition));

    private static Dictionary<string, string> CreateHeaders(
        int totalLength,
        bool supportsRanges,
        string? contentDisposition,
        long? start = null,
        long? end = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Length"] = start is not null
                ? (end!.Value - start.Value + 1).ToString(CultureInfo.InvariantCulture)
                : totalLength.ToString(CultureInfo.InvariantCulture),
            ["Content-Type"] = "application/octet-stream",
            ["ETag"] = "\"test-v1\"",
        };
        if (supportsRanges)
        {
            headers["Accept-Ranges"] = "bytes";
        }

        if (start is not null)
        {
            headers["Content-Range"] = $"bytes {start}-{end}/{totalLength}";
        }

        if (contentDisposition is not null)
        {
            headers["Content-Disposition"] = contentDisposition;
        }

        return headers;
    }
}

#pragma warning restore xUnit1030
