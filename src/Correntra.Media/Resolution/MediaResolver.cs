using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Correntra.Media.Dash;
using Correntra.Media.Hls;
using Correntra.Media.Models;
using Correntra.Media.Sites;
using Correntra.Media.Utilities;

namespace Correntra.Media.Resolution;

public sealed class MediaResolver : IMediaResolver
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    public MediaResolver(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<MediaDescriptor> ResolveAsync(MediaCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureWebUri(candidate.SourceUri);

        string extension = MediaText.FileExtension(candidate.SourceUri);
        string mime = NormalizeMime(candidate.MimeType);

        if (IsHls(extension, mime))
        {
            string manifestText = await ReadManifestAsync(candidate, cancellationToken).ConfigureAwait(false);
            return FromHls(candidate, HlsParser.Parse(candidate.SourceUri, manifestText));
        }

        if (IsDash(extension, mime))
        {
            string manifestText = await ReadManifestAsync(candidate, cancellationToken).ConfigureAwait(false);
            return FromDash(candidate, DashParser.Parse(candidate.SourceUri, manifestText));
        }

        if (GoogleVideoUrlParser.IsGoogleVideo(candidate.SourceUri))
        {
            return FromGoogleVideo(candidate, GoogleVideoUrlParser.Parse(candidate.SourceUri, mime));
        }

        return FromDirect(candidate, mime);
    }

    private async Task<string> ReadManifestAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.SourceUri);
        foreach ((string name, string value) in candidate.Headers)
        {
            if (!IsSafeHeader(name, value) || IsHopByHopHeader(name))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Referrer) && Uri.TryCreate(candidate.Referrer, UriKind.Absolute, out Uri? referrer))
        {
            request.Headers.Referrer = referrer;
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaximumManifestBytes)
        {
            throw new MediaResolutionException("Media manifest is larger than the supported limit.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > MaximumManifestBytes)
            {
                throw new MediaResolutionException("Media manifest is larger than the supported limit.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        Encoding encoding = GetEncoding(response.Content.Headers.ContentType?.CharSet);
        return encoding.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }

    private static MediaDescriptor FromHls(MediaCandidate candidate, HlsPlaylist playlist)
    {
        var variants = new List<MediaVariant>();
        var subtitles = new List<MediaSubtitle>();

        if (playlist.IsMaster)
        {
            for (int index = 0; index < playlist.Variants.Count; index++)
            {
                HlsVariant variant = playlist.Variants[index];
                variants.Add(new MediaVariant
                {
                    Id = MediaText.StableId("hls", variant.SourceUri.AbsoluteUri),
                    SourceUri = variant.SourceUri,
                    TrackKind = MediaTrackKind.Muxed,
                    DisplayName = variant.Name ?? QualityName(variant.Height, variant.Bandwidth),
                    MimeType = "application/vnd.apple.mpegurl",
                    Container = "HLS",
                    Codecs = variant.Codecs,
                    Width = variant.Width,
                    Height = variant.Height,
                    FrameRate = variant.FrameRate,
                    Bitrate = variant.AverageBandwidth ?? variant.Bandwidth,
                    AudioGroupId = variant.AudioGroupId,
                });
            }

            foreach (HlsRendition rendition in playlist.Renditions)
            {
                if (rendition.SourceUri is null)
                {
                    continue;
                }

                if (string.Equals(rendition.Type, "SUBTITLES", StringComparison.OrdinalIgnoreCase))
                {
                    subtitles.Add(new MediaSubtitle(
                        MediaText.StableId("sub", rendition.SourceUri.AbsoluteUri),
                        rendition.SourceUri,
                        rendition.Language,
                        rendition.Name,
                        "webvtt",
                        rendition.IsDefault));
                }
                else if (string.Equals(rendition.Type, "AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    variants.Add(new MediaVariant
                    {
                        Id = MediaText.StableId("audio", rendition.SourceUri.AbsoluteUri),
                        SourceUri = rendition.SourceUri,
                        TrackKind = MediaTrackKind.Audio,
                        DisplayName = rendition.Name,
                        MimeType = "application/vnd.apple.mpegurl",
                        Container = "HLS",
                        Language = rendition.Language,
                        AudioGroupId = rendition.GroupId,
                        IsDefault = rendition.IsDefault,
                    });
                }
            }
        }
        else
        {
            variants.Add(new MediaVariant
            {
                Id = MediaText.StableId("hls", playlist.SourceUri.AbsoluteUri),
                SourceUri = playlist.SourceUri,
                TrackKind = MediaTrackKind.Muxed,
                DisplayName = playlist.IsLive ? "Live" : "Original",
                MimeType = "application/vnd.apple.mpegurl",
                Container = "HLS",
                IsLive = playlist.IsLive,
                Protection = playlist.Protection,
                Segments = playlist.Segments,
                InitializationSegment = playlist.InitializationSegment,
            });
        }

        return new MediaDescriptor
        {
            Id = candidate.Id,
            Title = MediaText.GuessTitle(candidate.SourceUri, candidate.Title),
            SourceKind = MediaSourceKind.Hls,
            SourceUri = candidate.SourceUri,
            IsLive = playlist.IsLive,
            Protection = playlist.Protection,
            ProtectionReason = playlist.ProtectionReason,
            Duration = playlist.Segments.Count == 0
                ? null
                : TimeSpan.FromTicks(playlist.Segments.Sum(segment => segment.Duration?.Ticks ?? 0)),
            Variants = variants,
            Subtitles = subtitles,
            RequestHeaders = candidate.Headers,
        };
    }

    private static MediaDescriptor FromDash(MediaCandidate candidate, DashManifest manifest)
    {
        List<MediaVariant> variants = manifest.Representations
            .Where(representation => representation.TrackKind != MediaTrackKind.Subtitle)
            .Select(representation => new MediaVariant
            {
                Id = representation.Id,
                SourceUri = representation.BaseUri,
                TrackKind = representation.TrackKind,
                DisplayName = QualityName(representation.Height, representation.Bandwidth),
                MimeType = representation.MimeType,
                Container = "DASH",
                Codecs = representation.Codecs,
                Width = representation.Width,
                Height = representation.Height,
                FrameRate = representation.FrameRate,
                Bitrate = representation.Bandwidth,
                Language = representation.Language,
                IsLive = manifest.IsDynamic,
                Protection = representation.Protection,
                InitializationSegment = representation.InitializationSegment,
                Segments = representation.Segments,
            })
            .ToList();

        List<MediaSubtitle> subtitles = manifest.Representations
            .Where(representation => representation.TrackKind == MediaTrackKind.Subtitle)
            .Select(representation => new MediaSubtitle(
                representation.Id,
                representation.BaseUri,
                representation.Language,
                representation.Language,
                representation.MimeType,
                false))
            .ToList();

        return new MediaDescriptor
        {
            Id = candidate.Id,
            Title = MediaText.GuessTitle(candidate.SourceUri, candidate.Title),
            SourceKind = MediaSourceKind.Dash,
            SourceUri = candidate.SourceUri,
            IsLive = manifest.IsDynamic,
            Protection = manifest.Protection,
            ProtectionReason = manifest.ProtectionReason,
            Duration = manifest.Duration,
            Variants = variants,
            Subtitles = subtitles,
            RequestHeaders = candidate.Headers,
        };
    }

    private static MediaDescriptor FromGoogleVideo(MediaCandidate candidate, GoogleVideoInfo info)
    {
        MediaVariant variant = new()
        {
            Id = info.Itag is null ? candidate.Id : $"yt-{info.Itag}",
            SourceUri = candidate.SourceUri,
            TrackKind = info.TrackKind,
            DisplayName = info.DisplayName,
            MimeType = info.MimeType,
            Container = info.Container,
            Codecs = info.Codecs,
            Width = info.Width,
            Height = info.Height,
            Bitrate = info.Bitrate,
            ApproximateBytes = info.ContentLength ?? candidate.ContentLength,
        };

        return new MediaDescriptor
        {
            Id = candidate.Id,
            Title = MediaText.GuessTitle(candidate.SourceUri, candidate.Title),
            SourceKind = MediaSourceKind.BrowserObserved,
            SourceUri = candidate.SourceUri,
            Variants = [variant],
            RequestHeaders = candidate.Headers,
        };
    }

    private static MediaDescriptor FromDirect(MediaCandidate candidate, string mime)
    {
        MediaTrackKind trackKind = DetermineDirectTrack(mime, MediaText.FileExtension(candidate.SourceUri));
        string extension = MediaText.FileExtension(candidate.SourceUri).TrimStart('.');
        var variant = new MediaVariant
        {
            Id = candidate.Id,
            SourceUri = candidate.SourceUri,
            TrackKind = trackKind,
            DisplayName = "Original",
            MimeType = string.IsNullOrWhiteSpace(mime) ? null : mime,
            Container = string.IsNullOrWhiteSpace(extension) ? null : extension.ToUpperInvariant(),
            ApproximateBytes = candidate.ContentLength,
        };

        return new MediaDescriptor
        {
            Id = candidate.Id,
            Title = MediaText.GuessTitle(candidate.SourceUri, candidate.Title),
            SourceKind = MediaSourceKind.Direct,
            SourceUri = candidate.SourceUri,
            Variants = [variant],
            RequestHeaders = candidate.Headers,
        };
    }

    private static string NormalizeMime(string? mime)
    {
        return mime?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static bool IsHls(string extension, string mime)
    {
        return extension == ".m3u8" ||
               mime is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl";
    }

    private static bool IsDash(string extension, string mime)
    {
        return extension == ".mpd" || mime == "application/dash+xml";
    }

    private static MediaTrackKind DetermineDirectTrack(string mime, string extension)
    {
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            extension is ".mp4" or ".webm" or ".mkv" or ".mov" or ".avi")
        {
            return MediaTrackKind.Video;
        }

        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
            extension is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".flac" or ".wav")
        {
            return MediaTrackKind.Audio;
        }

        return MediaTrackKind.Unknown;
    }

    private static string QualityName(int? height, long? bitrate)
    {
        if (height is > 0)
        {
            return $"{height}p";
        }

        return bitrate is > 0 ? $"{bitrate.Value / 1000} kbps" : "Original";
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // UTF-8 is the required/default encoding for modern manifests.
            }
        }

        return new UTF8Encoding(false, true);
    }

    private static bool IsSafeHeader(string name, string value)
    {
        return name.Length is > 0 and <= 128 &&
               value.Length <= 16 * 1024 &&
               !name.Any(character => char.IsControl(character) || character is ':' or ' ') &&
               !value.Contains('\r') &&
               !value.Contains('\n');
    }

    private static bool IsHopByHopHeader(string name)
    {
        return name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Range", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWebUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new MediaResolutionException("Only HTTP and HTTPS media sources are supported.");
        }
    }
}
