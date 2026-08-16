using Correntra.Media.Models;
using Correntra.Media.Resolution;
using Correntra.Media.Utilities;

namespace Correntra.Media.Sites;

public sealed class ObservedMediaResolver
{
    private const int MaximumCandidates = 100;
    private readonly IMediaResolver _resolver;

    public ObservedMediaResolver(IMediaResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<MediaDescriptor> ResolveGroupAsync(
        IReadOnlyCollection<MediaCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is 0 or > MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), $"A media group must contain 1-{MaximumCandidates} candidates.");
        }

        List<MediaCandidate> distinct = candidates
            .Where(candidate => candidate.SourceUri.Scheme is "http" or "https")
            .GroupBy(candidate => CandidateKey(candidate.SourceUri), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.DetectedAt).First())
            .ToList();

        if (distinct.Count == 0)
        {
            throw new MediaResolutionException("The media group contains no supported web sources.");
        }

        if (distinct.All(candidate => GoogleVideoUrlParser.IsGoogleVideo(candidate.SourceUri)))
        {
            return ResolveGoogleVideoGroup(distinct);
        }

        MediaCandidate? manifest = distinct.FirstOrDefault(IsMasterCandidate) ??
                                   distinct.FirstOrDefault(IsManifestCandidate);
        if (manifest is not null)
        {
            return await _resolver.ResolveAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        MediaDescriptor[] descriptors = await Task.WhenAll(
            distinct.Select(candidate => _resolver.ResolveAsync(candidate, cancellationToken))).ConfigureAwait(false);
        MediaDescriptor first = descriptors[0];
        List<MediaVariant> variants = descriptors
            .SelectMany(descriptor => descriptor.Variants)
            .GroupBy(variant => variant.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(variant => variant.TrackKind)
            .ThenByDescending(variant => variant.Height)
            .ThenByDescending(variant => variant.Bitrate)
            .ToList();

        return first with
        {
            SourceKind = MediaSourceKind.BrowserObserved,
            Variants = variants,
            Subtitles = descriptors.SelectMany(descriptor => descriptor.Subtitles).DistinctBy(subtitle => subtitle.Id).ToList(),
        };
    }

    private static MediaDescriptor ResolveGoogleVideoGroup(IReadOnlyCollection<MediaCandidate> candidates)
    {
        MediaCandidate first = candidates.First();
        var variants = new Dictionary<string, MediaVariant>(StringComparer.Ordinal);

        foreach (MediaCandidate candidate in candidates.OrderByDescending(item => item.DetectedAt))
        {
            GoogleVideoInfo info = GoogleVideoUrlParser.Parse(candidate.SourceUri, candidate.MimeType);
            string id = info.Itag is null ? candidate.Id : $"yt-{info.Itag}";
            variants.TryAdd(id, new MediaVariant
            {
                Id = id,
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
            });
        }

        return new MediaDescriptor
        {
            Id = MediaText.StableId("youtube", first.PageUri?.AbsoluteUri ?? first.SourceUri.AbsoluteUri),
            Title = MediaText.GuessTitle(first.SourceUri, first.Title),
            SourceKind = MediaSourceKind.BrowserObserved,
            SourceUri = first.PageUri ?? first.SourceUri,
            Variants = variants.Values
                .OrderBy(variant => variant.TrackKind)
                .ThenByDescending(variant => variant.Height)
                .ThenByDescending(variant => variant.Bitrate)
                .ToList(),
            RequestHeaders = first.Headers,
        };
    }

    private static bool IsMasterCandidate(MediaCandidate candidate)
    {
        string path = candidate.SourceUri.AbsolutePath;
        return IsManifestCandidate(candidate) &&
               (path.Contains("master", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("manifest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsManifestCandidate(MediaCandidate candidate)
    {
        string extension = MediaText.FileExtension(candidate.SourceUri);
        string mime = candidate.MimeType?.Split(';', 2)[0].Trim() ?? string.Empty;
        return extension is ".m3u8" or ".mpd" ||
               mime is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "application/dash+xml";
    }

    private static string CandidateKey(Uri uri)
    {
        var builder = new UriBuilder(uri);
        string[] ignored = ["range", "rn", "rbuf", "ump", "cpn"];
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string item in builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = item.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0]);
            if (!ignored.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                pairs.Add(new KeyValuePair<string, string>(key, parts.Length == 2 ? parts[1] : string.Empty));
            }
        }

        builder.Query = string.Join('&', pairs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={pair.Value}"));
        return builder.Uri.AbsoluteUri;
    }
}

