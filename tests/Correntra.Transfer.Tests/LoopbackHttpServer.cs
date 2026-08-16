using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Correntra.Transfer.Tests;

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentBag<Task> connections = new();
    private readonly Func<LoopbackRequest, NetworkStream, Task> handler;
    private readonly Task acceptLoop;
    private int requestCount;

    public LoopbackHttpServer(Func<LoopbackRequest, NetworkStream, Task> handler)
    {
        this.handler = handler;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/", UriKind.Absolute);
        acceptLoop = AcceptConnectionsAsync();
    }

    public Uri BaseUri { get; }

    public int RequestCount => Volatile.Read(ref requestCount);

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        await Task.WhenAll(connections.ToArray()).ConfigureAwait(false);
        shutdown.Dispose();
    }

    public static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string>? headers = null,
        ReadOnlyMemory<byte> body = default,
        bool includeContentLength = true)
    {
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(statusCode.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(reasonPhrase)
            .Append("\r\n");

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        if (includeContentLength && (headers is null || !headers.ContainsKey("Content-Length")))
        {
            builder.Append("Content-Length: ")
                .Append(body.Length.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString())).ConfigureAwait(false);
        if (!body.IsEmpty)
        {
            await stream.WriteAsync(body).ConfigureAwait(false);
        }
    }

    public static async Task WriteChunkedResponseAsync(NetworkStream stream, ReadOnlyMemory<byte> body)
    {
        const string headers = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
        const int chunkSize = 4096;
        for (var offset = 0; offset < body.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, body.Length - offset);
            var prefix = Encoding.ASCII.GetBytes(length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            await stream.WriteAsync(prefix).ConfigureAwait(false);
            await stream.WriteAsync(body.Slice(offset, length)).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray()).ConfigureAwait(false);
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
    }

    public static (long Start, long End)? ParseRange(LoopbackRequest request)
    {
        if (!request.Headers.TryGetValue("Range", out var value) ||
            !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pieces = value[6..].Split('-', 2);
        if (pieces.Length != 2 ||
            !long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end))
        {
            return null;
        }

        return (start, end);
    }

    private async Task AcceptConnectionsAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (shutdown.IsCancellationRequested)
            {
                break;
            }

            var task = HandleConnectionAsync(client);
            connections.Add(task);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, shutdown.Token).ConfigureAwait(false);
                if (request is not null)
                {
                    Interlocked.Increment(ref requestCount);
                    await handler(request, stream).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // Tests intentionally abort clients and servers to exercise retry behavior.
            }
            catch (SocketException)
            {
                // The peer can close while a deliberately slow test response is in flight.
            }
        }
    }

    private static async Task<LoopbackRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        const int maximumHeaderBytes = 64 * 1024;
        using var memory = new MemoryStream();
        var singleByte = new byte[1];
        var terminatorState = 0;

        while (memory.Length < maximumHeaderBytes)
        {
            var read = await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var value = singleByte[0];
            memory.WriteByte(value);
            terminatorState = (terminatorState, value) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
            if (terminatorState == 4)
            {
                break;
            }
        }

        if (terminatorState != 4)
        {
            throw new IOException("The HTTP request headers were too large or incomplete.");
        }

        var text = Encoding.ASCII.GetString(memory.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length != 3)
        {
            throw new IOException("The HTTP request line was malformed.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon]] = line[(colon + 1)..].Trim();
            }
        }

        return new LoopbackRequest(requestLine[0], requestLine[1], headers);
    }
}

internal sealed record LoopbackRequest(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers);

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Correntra.Transfer.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
