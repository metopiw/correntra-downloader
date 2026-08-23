using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Correntra.Core;
using Correntra.Core.Downloads;
using Correntra.Core.Ipc;
using Correntra.Core.Security;
using Correntra.Media.Models;
using Correntra.Media.Resolution;
using Correntra.Media.Sites;

namespace Correntra.Agent.Runtime;

public sealed partial class AgentCommandDispatcher
{
    private const int MaximumRequestIdLength = 128;
    private static readonly string AgentVersion =
        typeof(AgentCommandDispatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AgentCommandDispatcher).Assembly.GetName().Version?.ToString()
        ?? "0.1.0";
    private readonly DownloadJobCoordinator _coordinator;
    private readonly IDesktopLauncher _desktopLauncher;
    private readonly IMediaResolver _mediaResolver;
    private readonly YtDlpExecutor _ytDlpExecutor;

    public AgentCommandDispatcher(
        DownloadJobCoordinator coordinator,
        IDesktopLauncher desktopLauncher,
        IMediaResolver? mediaResolver = null,
        YtDlpExecutor? ytDlpExecutor = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _desktopLauncher = desktopLauncher ?? throw new ArgumentNullException(nameof(desktopLauncher));
        _mediaResolver = mediaResolver ?? new MediaResolver(new HttpClient());
        _ytDlpExecutor = ytDlpExecutor ?? new YtDlpExecutor();
    }

    public async Task<AgentResponseEnvelope> DispatchAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string requestId = IsValidRequestId(request.RequestId) ? request.RequestId : "invalid";
        try
        {
            ValidateEnvelope(request);
            return request.Kind switch
            {
                "host.ping" or "ping" => AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion),
                "agent.snapshot.get" => await SnapshotAsync(request, cancellationToken).ConfigureAwait(false),
                "takeover.offer" => await CreateFromTakeoverAsync(request, cancellationToken).ConfigureAwait(false),
                "media.start" => await CreateFromMediaAsync(request, cancellationToken).ConfigureAwait(false),
                "media.resolve" => await ResolveMediaAsync(request, cancellationToken).ConfigureAwait(false),
                "download.create" => await CreateFromDesktopAsync(request, cancellationToken).ConfigureAwait(false),
                "download.pause" => await ChangeJobAsync(request, _coordinator.PauseAsync, cancellationToken).ConfigureAwait(false),
                "download.resume" => await ChangeJobAsync(request, _coordinator.ResumeAsync, cancellationToken).ConfigureAwait(false),
                "download.cancel" => await ChangeJobAsync(request, _coordinator.CancelAsync, cancellationToken).ConfigureAwait(false),
                "download.retry" => await ChangeJobAsync(request, _coordinator.RetryAsync, cancellationToken).ConfigureAwait(false),
                "download.confirm" => await ConfirmAsync(request, cancellationToken).ConfigureAwait(false),
                "download.remove" => await RemoveAsync(request, cancellationToken).ConfigureAwait(false),
                "queue.start" => await ChangeMainQueueAsync(request, start: true, cancellationToken).ConfigureAwait(false),
                "queue.stop" => await ChangeMainQueueAsync(request, start: false, cancellationToken).ConfigureAwait(false),
                _ => AgentResponseEnvelope.Rejected(request.RequestId, "unsupported-command", AgentVersion),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or JsonException)
        {
            return AgentResponseEnvelope.Rejected(requestId, "invalid-request", AgentVersion);
        }
    }

    private async Task<AgentResponseEnvelope> SnapshotAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        AgentSnapshot snapshot = await _coordinator.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, snapshot: snapshot);
    }

    private async Task<AgentResponseEnvelope> CreateFromTakeoverAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        Uri source = ReadOptionalHttpUri(payload, "finalUrl") ?? ReadRequiredHttpUri(payload, "url");
        string fileName = ReadOptionalString(payload, "filename", 260) ?? InferFileName(source, "download.bin");
        if (IsGenericFileName(fileName))
        {
            // Browsers occasionally report a placeholder name before the real
            // one is known; prefer a name derived from the URL path.
            fileName = InferFileName(source, fileName);
        }

        if (IsGenericFileName(fileName))
        {
            // CDN redirects strip extensions from the final URL; the original
            // page URL often still carries the real file name.
            Uri? original = ReadOptionalHttpUri(payload, "url");
            if (original is not null)
            {
                fileName = InferFileName(original, fileName);
            }
        }
        Dictionary<string, string> headers = ReadHeaders(payload, "headers");
        AddReferrer(payload, headers);
        string destination = DefaultDestination("General");
        var creation = new AgentJobCreation(
            source,
            fileName,
            destination,
            StartImmediately: false,
            NeedsUserConfirmation: true,
            headers,
            DateTimeOffset.UtcNow.AddHours(12));
        AgentJobRecord job = await _coordinator.CreateAsync(creation, cancellationToken).ConfigureAwait(false);
        // Do not block the takeover response on the desktop process starting or
        // the confirmation dialog appearing. The browser must be told to cancel
        // its own download immediately; otherwise Chrome keeps downloading for
        // the full desktop-launch delay before handing off.
        _ = _desktopLauncher.ShowDownloadConfirmationAsync(job.Id, CancellationToken.None);
        return AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, job.Id.ToString());
    }

    private async Task<AgentResponseEnvelope> ResolveMediaAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        Uri source = ReadRequiredHttpUri(payload, "url");
        Dictionary<string, string> headers = ReadHeaders(payload, "headers");
        AddReferrer(payload, headers);

        // Social platforms need the page URL for extraction; prefer it whenever
        // the video engine is available and the page belongs to a known site.
        Uri? pageUrl = ReadOptionalHttpUri(payload, "pageUrl");
        Uri? ytDlpTarget = SelectYtDlpTarget(source, pageUrl);
        if (ytDlpTarget is not null)
        {
            try
            {
                YtDlpInfo info = await _ytDlpExecutor.EnumerateFormatsAsync(
                    ytDlpTarget.AbsoluteUri,
                    cancellationToken).ConfigureAwait(false);
                List<MediaQualityOption> options = RankQualities(info.Options
                    .Select(option => new MediaQualityOption(
                        option.Id,
                        option.DisplayName,
                        option.IsAudioOnly ? "audio" : "mp4",
                        option.Height,
                        null,
                        null)));
                if (options.Count > 0)
                {
                    return AgentResponseEnvelope.Accepted(
                        request.RequestId,
                        AgentVersion,
                        mediaQualities: options);
                }

                return AgentResponseEnvelope.Rejected(request.RequestId, "media-resolve-failed", AgentVersion);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or HttpRequestException or JsonException or IOException)
            {
                // The manifest resolver below only understands real media files
                // and manifests. Falling back with an HTML watch page produced a
                // bogus descriptor whose variants were all filtered out, so the
                // extension read an accepted-but-empty reply as "Kalite
                // bulunamadı" instead of the actual extraction failure.
                // Sniffed CDN URLs (.m3u8/.mp4 fragments) stay fallback-eligible
                // because the manifest itself is genuinely fetchable there.
                bool sourceIsFetchableMedia = YtDlpExecutor.LooksLikeDirectMedia(source) ||
                    GoogleVideoUrlParser.IsGoogleVideo(source);
                if (!sourceIsFetchableMedia)
                {
                    return AgentResponseEnvelope.Rejected(
                        request.RequestId,
                        MapEnumerationFailure(exception),
                        AgentVersion);
                }
            }
        }

        var candidate = new MediaCandidate
        {
            Id = ReadOptionalString(payload, "candidateId", 64) ?? "resolve",
            SourceUri = source,
            Title = ReadOptionalString(payload, "title", 500),
            Referrer = ReadOptionalString(payload, "referrer", 16_384),
            Headers = headers,
        };

        MediaDescriptor descriptor;
        try
        {
            descriptor = await _mediaResolver.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (DrmProtectedMediaException)
        {
            return AgentResponseEnvelope.Rejected(request.RequestId, "drm-protected", AgentVersion);
        }
        catch (MediaResolutionException)
        {
            return AgentResponseEnvelope.Rejected(request.RequestId, "media-resolve-failed", AgentVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            _ = exception;
            return AgentResponseEnvelope.Rejected(request.RequestId, "media-resolve-failed", AgentVersion);
        }

        List<MediaQualityOption> qualities = RankQualities(descriptor.Variants
            .Where(variant => variant.TrackKind is MediaTrackKind.Video or MediaTrackKind.Muxed or MediaTrackKind.Audio)
            .Select(variant => new MediaQualityOption(
                variant.Id,
                variant.DisplayName ?? QualityFallback(variant),
                variant.Container ?? "mp4",
                variant.Height,
                variant.Bitrate,
                variant.MimeType)));

        return AgentResponseEnvelope.Accepted(
            request.RequestId,
            AgentVersion,
            mediaQualities: qualities);
    }

    /// <summary>
    /// IDM lists highest video first and parks audio-only at the bottom.
    /// Duplicate heights from paired video+audio tracks collapse to one row.
    /// </summary>
    internal static List<MediaQualityOption> RankQualities(IEnumerable<MediaQualityOption> options)
    {
        return options
            .GroupBy(static option => option.Height is > 0
                ? "v:" + option.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "a:" + option.Id, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(option => option.Bitrate ?? 0).First())
            .OrderBy(static option => option.Height is > 0 ? 0 : 1)
            .ThenByDescending(static option => option.Height ?? 0)
            .ThenByDescending(static option => option.Bitrate ?? 0)
            .ToList();
    }

    private static string QualityFallback(MediaVariant variant)
    {
        return variant.Height is > 0
            ? $"{variant.Height}p"
            : variant.Bitrate is > 0
                ? $"{variant.Bitrate.Value / 1000} kbps"
                : "Original";
    }

    /// <summary>
    /// Translates the most common yt-dlp extraction failures into bridge
    /// reasons the extension can explain. Instagram answers login-walled posts
    /// with an opaque "empty media response" and X throttles guest tokens with
    /// HTTP 429; both previously surfaced as a generic failure.
    /// </summary>
    internal static string MapEnumerationFailure(Exception exception)
    {
        string message = exception.Message;
        if (message.Contains("empty media response", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("--cookies-from-browser", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("pass cookies", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("members-only", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("private video", StringComparison.OrdinalIgnoreCase))
        {
            return "media-login-required";
        }

        if (message.Contains("429", StringComparison.Ordinal) ||
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
        {
            return "media-rate-limited";
        }

        return "media-resolve-failed";
    }

    private Uri? SelectYtDlpTarget(Uri source, Uri? pageUrl)
    {
        if (!_ytDlpExecutor.IsAvailable || !YtDlpExecutor.ShouldExtractWithYtDlp(source, pageUrl))
        {
            return null;
        }

        // Feed vs permalink: the overlay used to send the generic feed URL as
        // pageUrl while the real post lives at /reels/<id> etc. On Instagram's
        // main feed location.href is https://www.instagram.com/ but the video's
        // permalink is https://www.instagram.com/reels/<id>/ — picking the
        // feed fails extraction with "Liste alınamadı" while the permalink
        // succeeds. Prefer the more specific post permalink when one side is a
        // post and the other is a feed/homepage.
        if (pageUrl is not null &&
            YtDlpExecutor.IsSupportedHost(pageUrl) &&
            YtDlpExecutor.IsSupportedHost(source))
        {
            bool sourceIsPost = IsPostPermalink(source);
            bool pageIsPost = IsPostPermalink(pageUrl);
            if (sourceIsPost && !pageIsPost)
            {
                return source;
            }

            if (!sourceIsPost && pageIsPost)
            {
                return pageUrl;
            }

            // Both posts or both generic: prefer the longer, more specific path.
            if (source.AbsolutePath.Length > pageUrl.AbsolutePath.Length + 8)
            {
                return source;
            }

            if (pageUrl.AbsolutePath.Length > source.AbsolutePath.Length + 8)
            {
                return pageUrl;
            }
        }

        if (pageUrl is not null &&
            (YtDlpExecutor.IsSupportedHost(pageUrl) ||
             YtDlpExecutor.IsFragmentCdn(source) ||
             !YtDlpExecutor.LooksLikeDirectMedia(source)))
        {
            return pageUrl;
        }

        return YtDlpExecutor.IsSupportedHost(source) ? source : pageUrl ?? source;
    }

    private static bool IsPostPermalink(Uri uri)
    {
        string path = uri.AbsolutePath;
        if (path.Length <= 1)
        {
            return false;
        }

        return PostPermalinkPattern().IsMatch(path);
    }

    private async Task<AgentResponseEnvelope> CreateFromMediaAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        Uri source = ReadRequiredHttpUri(payload, "url");
        Uri? pageUrl = ReadOptionalHttpUri(payload, "pageUrl");
        string category = "Video";
        string title = "media";
        string? container = null;
        string? formatId = null;
        if (payload.TryGetProperty("media", out JsonElement media))
        {
            media = RequireObject(media);
            string? kind = ReadOptionalString(media, "kind", 20);
            category = string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase) ? "Music" : "Video";
            title = ReadOptionalString(media, "title", 500) ?? title;
            container = ReadOptionalString(media, "container", 16);
            formatId = ReadOptionalFormatSelector(media);
        }

        // When the page belongs to a social platform and the video engine is
        // installed, the page URL is the extraction input; the raw observed
        // segment URL would only fetch a fragment.
        Uri? ytDlpTarget = SelectYtDlpTarget(source, pageUrl);
        if (ytDlpTarget is not null)
        {
            source = ytDlpTarget;
            if (string.Equals(container, "m3u8", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(container, "mpd", StringComparison.OrdinalIgnoreCase))
            {
                container = null;
            }

            // The coordinator only routes unknown sites through yt-dlp when this
            // header is present; a missing selector would HTTP-GET the HTML page.
            formatId ??= "bestvideo+bestaudio/best";
        }

        string extension = NormalizeExtension(container) ?? GetExtensionFromUri(source) ?? (category == "Music" ? ".m4a" : ".mp4");
        string fileName = SafePath.SanitizeFileName(title + extension);
        Dictionary<string, string> headers = ReadNestedHeaders(payload);
        AddReferrer(payload, headers);
        if (formatId is not null && ytDlpTarget is not null)
        {
            headers[YtDlpExecutor.FormatHeader] = formatId;
            if (formatId.Contains("bestaudio", StringComparison.OrdinalIgnoreCase) &&
                !formatId.Contains("bestvideo", StringComparison.OrdinalIgnoreCase))
            {
                category = "Music";
                if (string.Equals(Path.GetExtension(fileName), ".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = SafePath.SanitizeFileName(Path.GetFileNameWithoutExtension(fileName) + ".m4a");
                }
            }
        }

        var creation = new AgentJobCreation(
            source,
            fileName,
            DefaultDestination(category),
            StartImmediately: false,
            NeedsUserConfirmation: true,
            headers,
            DateTimeOffset.UtcNow.AddHours(12));
        AgentJobRecord job = await _coordinator.CreateAsync(creation, cancellationToken).ConfigureAwait(false);
        _ = _desktopLauncher.ShowDownloadConfirmationAsync(job.Id, CancellationToken.None);
        return AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, job.Id.ToString());
    }

    private async Task<AgentResponseEnvelope> CreateFromDesktopAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        Uri source = ReadRequiredHttpUri(payload, "url");
        string fileName = ReadOptionalString(payload, "fileName", 260) ?? InferFileName(source, "download.bin");
        string destination = ReadOptionalString(payload, "destinationDirectory", 32_768) ?? DefaultDestination("General");
        bool startImmediately = ReadOptionalBoolean(payload, "startImmediately") ?? true;
        Dictionary<string, string> headers = ReadHeaders(payload, "headers");
        AddReferrer(payload, headers);
        AgentJobRecord job = await _coordinator.CreateAsync(
            new AgentJobCreation(source, fileName, destination, startImmediately, false, headers),
            cancellationToken).ConfigureAwait(false);
        return AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, job.Id.ToString());
    }

    private static async Task<AgentResponseEnvelope> ChangeJobAsync(
        AgentRequestEnvelope request,
        Func<JobId, CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        string value = ReadRequiredString(payload, "jobId", 64);
        if (!JobId.TryParse(value, out JobId jobId))
        {
            throw new ArgumentException("The job ID is invalid.", nameof(request));
        }

        bool changed = await operation(jobId, cancellationToken).ConfigureAwait(false);
        return changed
            ? AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, jobId.ToString())
            : AgentResponseEnvelope.Rejected(request.RequestId, "job-state-conflict", AgentVersion);
    }

    private async Task<AgentResponseEnvelope> ConfirmAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        JobId jobId = ReadJobId(payload);
        bool startImmediately = ReadOptionalBoolean(payload, "startImmediately")
            ?? throw new InvalidDataException("'startImmediately' is required.");
        bool changed = await _coordinator.ConfirmAsync(jobId, startImmediately, cancellationToken).ConfigureAwait(false);
        return changed
            ? AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, jobId.ToString())
            : AgentResponseEnvelope.Rejected(request.RequestId, "job-state-conflict", AgentVersion);
    }

    private async Task<AgentResponseEnvelope> RemoveAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        JsonElement payload = RequireObject(request.Payload);
        JobId jobId = ReadJobId(payload);
        bool deleteDownloadedFile = ReadOptionalBoolean(payload, "deleteDownloadedFile") ?? false;
        bool removed = await _coordinator.RemoveAsync(jobId, deleteDownloadedFile, cancellationToken).ConfigureAwait(false);
        return removed
            ? AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion, jobId.ToString())
            : AgentResponseEnvelope.Rejected(request.RequestId, "job-not-found", AgentVersion);
    }

    private async Task<AgentResponseEnvelope> ChangeMainQueueAsync(
        AgentRequestEnvelope request,
        bool start,
        CancellationToken cancellationToken)
    {
        _ = RequireObject(request.Payload);
        _ = start
            ? await _coordinator.StartMainQueueAsync(cancellationToken).ConfigureAwait(false)
            : await _coordinator.StopMainQueueAsync(cancellationToken).ConfigureAwait(false);
        return AgentResponseEnvelope.Accepted(request.RequestId, AgentVersion);
    }

    private static JobId ReadJobId(JsonElement payload)
    {
        string value = ReadRequiredString(payload, "jobId", 64);
        return JobId.TryParse(value, out JobId jobId)
            ? jobId
            : throw new InvalidDataException("The job ID is invalid.");
    }

    private static void ValidateEnvelope(AgentRequestEnvelope request)
    {
        if (request.ProtocolVersion != 1 || !IsValidRequestId(request.RequestId))
        {
            throw new InvalidDataException("The Agent request envelope is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.Kind) || request.Kind.Length > 80 || request.Kind.Any(char.IsControl))
        {
            throw new InvalidDataException("The Agent request kind is invalid.");
        }

        if (request.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The Agent request timestamp must be UTC.");
        }

        RequireObject(request.Payload);
    }

    private static JsonElement RequireObject(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException("The payload must be a JSON object.");
    }

    private static Uri ReadRequiredHttpUri(JsonElement payload, string name) =>
        ReadOptionalHttpUri(payload, name) ?? throw new InvalidDataException($"'{name}' is required.");

    private static Uri? ReadOptionalHttpUri(JsonElement payload, string name)
    {
        string? value = ReadOptionalString(payload, name, 16_384);
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Only absolute HTTP and HTTPS URLs are accepted.");
        }

        return uri;
    }

    private static string ReadRequiredString(JsonElement payload, string name, int maximumLength) =>
        ReadOptionalString(payload, name, maximumLength) ?? throw new InvalidDataException($"'{name}' is required.");

    private static string? ReadOptionalString(JsonElement payload, string name, int maximumLength)
    {
        if (!payload.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"'{name}' must be a string.");
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(static character => character is '\r' or '\n' or '\0'))
        {
            throw new InvalidDataException($"'{name}' is invalid.");
        }

        return text;
    }

    private static bool? ReadOptionalBoolean(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"'{name}' must be a boolean."),
        };
    }

    private static string? ReadOptionalFormatSelector(JsonElement media)
    {
        string? formatId = ReadOptionalString(media, "formatId", 220);
        if (formatId is null)
        {
            return null;
        }

        // The selector is forwarded to yt-dlp as a process argument; restrict
        // it to the characters format expressions actually use.
        return FormatSelectorRegex().IsMatch(formatId) ? formatId : null;
    }

    private static Dictionary<string, string> ReadNestedHeaders(JsonElement payload)
    {
        if (!payload.TryGetProperty("authContext", out JsonElement context) || context.ValueKind == JsonValueKind.Null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        context = RequireObject(context);
        return ReadHeaders(context, "headers");
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement payload, string name)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!payload.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return headers;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("HTTP headers must be a JSON object.");
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("HTTP header values must be strings.");
            }

            string headerValue = property.Value.GetString() ?? string.Empty;
            HttpHeaderSet.ValidateName(property.Name);
            HttpHeaderSet.ValidateValue(headerValue, property.Name);
            if (IsManagedHeader(property.Name))
            {
                continue;
            }

            if (!headers.TryAdd(property.Name, headerValue))
            {
                throw new InvalidDataException("Duplicate HTTP headers are not accepted.");
            }
        }

        _ = new HttpHeaderSet(headers);
        return headers;
    }

    private static void AddReferrer(JsonElement payload, Dictionary<string, string> headers)
    {
        Uri? referrer = ReadOptionalHttpUri(payload, "referrer");
        if (referrer is null || headers.ContainsKey("Referer"))
        {
            return;
        }

        headers.Add("Referer", referrer.AbsoluteUri);
    }

    private static bool IsManagedHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private static string InferFileName(Uri source, string fallback)
    {
        string candidate = Uri.UnescapeDataString(Path.GetFileName(source.AbsolutePath));
        return string.IsNullOrWhiteSpace(candidate) ? fallback : SafePath.SanitizeFileName(candidate);
    }

    private static bool IsGenericFileName(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length is < 2 or > 10;
    }

    private static string? GetExtensionFromUri(Uri source)
    {
        string extension = Path.GetExtension(source.AbsolutePath);
        return extension is { Length: > 1 and <= 10 } ? extension.ToLowerInvariant() : null;
    }

    private static string? NormalizeExtension(string? container)
    {
        if (container is null || !ContainerNameRegex().IsMatch(container))
        {
            return null;
        }

        return "." + container.ToLowerInvariant();
    }

    private static string DefaultDestination(string category) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "Correntra",
        category);

    private static bool IsValidRequestId(string? requestId) =>
        requestId is { Length: > 0 and <= MaximumRequestIdLength } && RequestIdRegex().IsMatch(requestId);

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdRegex();

    [GeneratedRegex("^[A-Za-z0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9+\[\]().,_<=/-]{1,220}$", RegexOptions.CultureInvariant)]
    private static partial Regex FormatSelectorRegex();

    [GeneratedRegex(@"/(p|reels?|tv|shorts)/[A-Za-z0-9_-]+|/[^/]+/status/\d+|/i/status/\d+|/@[^/]+/video/\d+|/(reel|watch)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostPermalinkPattern();
}
