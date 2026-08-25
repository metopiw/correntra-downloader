using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Correntra.Desktop.Services;

/// <summary>Result of a VirusTotal lookup for one file.</summary>
public sealed record VirusTotalReport(int Detections, int Engines)
{
    public bool IsClean => Detections == 0;
}

/// <summary>
/// Optional on-demand malware reputation check against the VirusTotal v3
/// API. The key stays local (DesktopSettings); only the file's SHA-256 is
/// ever uploaded — never the file itself. Public-tier rate limit is four
/// lookups per minute, so results are cached per hash for this session.
/// </summary>
public static class VirusTotalService
{
    private const string ApiBase = "https://www.virustotal.com/api/v3/files/";

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, VirusTotalReport> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsConfigured(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    /// <summary>Hashes the file and asks VirusTotal; null means "not indexed yet".</summary>
    public static async Task<VirusTotalReport?> ScanFileAsync(
        string filePath, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The download could not be found.", filePath);
        }

        if (!IsConfigured(apiKey))
        {
            throw new InvalidOperationException("A VirusTotal API key has not been configured.");
        }

        await using FileStream stream = File.OpenRead(filePath);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

        if (Cache.TryGetValue(hash, out VirusTotalReport? cached))
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + hash);
        request.Headers.Add("x-apikey", apiKey!.Trim());

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // This exact file was never submitted to VirusTotal.
        }

        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        ReportDto? dto = await JsonSerializer.DeserializeAsync<ReportDto>(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        StatsDto? stats = dto?.Data?.Attributes?.LastAnalysisStats;
        if (stats is null || stats.Total == 0)
        {
            return null;
        }

        var report = new VirusTotalReport(stats.Malicious + stats.Suspicious, stats.Total);
        Cache[hash] = report;
        return report;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed record ReportDto([property: JsonPropertyName("data")] DataDto? Data);

    private sealed record DataDto([property: JsonPropertyName("attributes")] AttributesDto? Attributes);

    private sealed record AttributesDto(
        [property: JsonPropertyName("last_analysis_stats")] StatsDto? LastAnalysisStats);

    private sealed record StatsDto(
        [property: JsonPropertyName("malicious")] int Malicious,
        [property: JsonPropertyName("suspicious")] int Suspicious,
        [property: JsonPropertyName("total")] int Total);
}
