using System.Net;
using System.Text;
using System.Text.Json;

namespace Correntra.Agent.Runtime;

/// <summary>
/// Loopback-only HTTP bridge between the browser extension and the agent,
/// in the style of classic download managers. Native messaging proved too
/// fragile across Chrome versions/registrations; a localhost endpoint is
/// fully observable and testable with plain curl.
/// </summary>
public sealed class AgentLocalHttpServer
{
    public const int Port = 27410;
    private const int MaximumBodyBytes = 128 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AgentCommandDispatcher _dispatcher;

    public AgentLocalHttpServer(AgentCommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
        {
            // Another agent instance already owns the port; stay silent.
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    continue;
                }

                _ = Task.Run(() => HandleSafeAsync(context), CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (Exception)
            {
                // Shutdown must never throw.
            }
        }
    }

    private async Task HandleSafeAsync(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string? origin = context.Request.Headers["Origin"];
        // Browsers always send an Origin for cross-origin fetches; a web page
        // origin must never reach the bridge. Local tools (curl, tests) send
        // none and the extension sends chrome-extension://.
        if (origin is not null && !origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        // DNS-rebinding guard: a rebinding page resolves its own hostname to
        // 127.0.0.1, but the request still carries the attacker's Host header;
        // only the bridge's own loopback names are accepted. (The Origin rule
        // above already blocks browser CSRF; this closes the residual gap for
        // tooling that omits Origin.)
        string? host = context.Request.Headers["Host"];
        if (!string.Equals(host, "127.0.0.1:27410", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "localhost:27410", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        ApplyCors(context.Response, origin);

        string path = context.Request.Url?.AbsolutePath ?? "/";
        if (context.Request.HttpMethod == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        if (path == "/ping" && context.Request.HttpMethod == "GET")
        {
            await WriteJsonAsync(context, new { healthy = true }).ConfigureAwait(false);
            return;
        }

        if (path == "/jobs" && context.Request.HttpMethod == "GET")
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            AgentResponseEnvelope snapshot = await DispatchRawAsync("agent.snapshot.get", empty.RootElement.Clone()).ConfigureAwait(false);
            object jobs = snapshot.Payload.Snapshot is null
                ? Array.Empty<object>()
                : snapshot.Payload.Snapshot.Jobs.Select(static job => new
                {
                    id = job.Id.ToString(),
                    state = (int)job.State,
                    fileName = job.FileName,
                    bytesTransferred = job.BytesTransferred,
                    totalBytes = job.TotalBytes,
                }).ToArray();
            await WriteJsonAsync(context, new { jobs }).ConfigureAwait(false);
            return;
        }

        if (context.Request.HttpMethod == "POST" &&
            path is "/takeover" or "/confirm" or "/media/resolve" or "/media/start")
        {
            byte[] body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            if (body.Length == 0)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            string kind = path switch
            {
                "/takeover" => "takeover.offer",
                "/confirm" => "download.confirm",
                "/media/resolve" => "media.resolve",
                "/media/start" => "media.start",
                _ => "unsupported-command",
            };

            using JsonDocument document = JsonDocument.Parse(body);
            AgentResponseEnvelope response = await DispatchRawAsync(kind, document.RootElement.Clone())
                .ConfigureAwait(false);
            await WriteJsonAsync(context, response.Payload).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }

    private static void ApplyCors(HttpListenerResponse response, string? origin)
    {
        if (origin is null || !origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        response.Headers.Set("Access-Control-Allow-Origin", origin);
        response.Headers.Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Set("Access-Control-Allow-Headers", "content-type");
        response.Headers.Set("Access-Control-Allow-Private-Network", "true");
        response.Headers.Set("Vary", "Origin");
    }

    private async Task<AgentResponseEnvelope> DispatchRawAsync(string kind, JsonElement payload)
    {
        var envelope = new AgentRequestEnvelope(
            1,
            kind,
            "local_" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            payload);
        return await _dispatcher.DispatchAsync(envelope).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 is < 0 or > MaximumBodyBytes)
        {
            return Array.Empty<byte>();
        }

        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > MaximumBodyBytes)
            {
                return Array.Empty<byte>();
            }
        }

        return memory.ToArray();
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = json.Length;
        await context.Response.OutputStream.WriteAsync(json).ConfigureAwait(false);
        context.Response.Close();
    }
}
