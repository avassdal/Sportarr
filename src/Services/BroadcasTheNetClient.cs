using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// JSON-RPC request for BroadcasTheNet API
/// </summary>
public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public List<object> Params { get; set; } = new();

    // Sonarr uses a random 8-char GUID substring (string, not int).
    // BTN's PHP validates the id type, so match exactly.
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString()[..8];
}

/// <summary>
/// JSON-RPC response from BroadcasTheNet API
/// </summary>
public class JsonRpcResponse<T>
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "";

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

/// <summary>
/// JSON-RPC error details
/// </summary>
public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// BTN torrent query parameters.
/// Null/default properties are omitted (matches Sonarr's Newtonsoft DefaultValueHandling.Ignore).
/// Field names are PascalCase — BTN's PHP API is case-sensitive.
/// All fields mirror Sonarr's BroadcastheNetTorrentQuery exactly.
/// </summary>
public class BroadcastheNetTorrentQuery
{
    [JsonPropertyName("Id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("Category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    [JsonPropertyName("Name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("Search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Search { get; set; }

    [JsonPropertyName("Codec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? Codec { get; set; }

    [JsonPropertyName("Container")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? Container { get; set; }

    [JsonPropertyName("Source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? Source { get; set; }

    [JsonPropertyName("Resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? Resolution { get; set; }

    [JsonPropertyName("Origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? Origin { get; set; }

    [JsonPropertyName("Hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }

    [JsonPropertyName("Tvdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tvdb { get; set; }

    [JsonPropertyName("Tvrage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tvrage { get; set; }

    [JsonPropertyName("Age")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Age { get; set; }
}

/// <summary>
/// BTN torrents response container — matches Sonarr's BroadcastheNetTorrents exactly.
/// </summary>
public class BroadcastheNetTorrents
{
    [JsonPropertyName("Results")]
    public int Results { get; set; }

    [JsonPropertyName("Torrents")]
    public Dictionary<int, BroadcastheNetTorrent>? Torrents { get; set; }
}

/// <summary>
/// Individual BTN torrent details — field types match Sonarr's BroadcastheNetTorrent exactly.
/// BTN sends TorrentID/GroupID/TvdbID/TvrageID as JSON integers, not strings.
/// </summary>
public class BroadcastheNetTorrent
{
    [JsonPropertyName("GroupName")]
    public string? GroupName { get; set; }

    [JsonPropertyName("GroupID")]
    public int GroupId { get; set; }

    [JsonPropertyName("TorrentID")]
    public int TorrentId { get; set; }

    [JsonPropertyName("SeriesID")]
    public int SeriesId { get; set; }

    [JsonPropertyName("Series")]
    public string? Series { get; set; }

    [JsonPropertyName("SeriesBanner")]
    public string? SeriesBanner { get; set; }

    [JsonPropertyName("SeriesPoster")]
    public string? SeriesPoster { get; set; }

    [JsonPropertyName("YoutubeTrailer")]
    public string? YoutubeTrailer { get; set; }

    [JsonPropertyName("Category")]
    public string? Category { get; set; }

    [JsonPropertyName("Snatched")]
    public int? Snatched { get; set; }

    [JsonPropertyName("Seeders")]
    public int? Seeders { get; set; }

    [JsonPropertyName("Leechers")]
    public int? Leechers { get; set; }

    [JsonPropertyName("Source")]
    public string? Source { get; set; }

    [JsonPropertyName("Container")]
    public string? Container { get; set; }

    [JsonPropertyName("Codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("Resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("Origin")]
    public string? Origin { get; set; }

    [JsonPropertyName("ReleaseName")]
    public string ReleaseName { get; set; } = "";

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    [JsonPropertyName("Time")]
    public long Time { get; set; }

    [JsonPropertyName("TvdbID")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("TvrageID")]
    public int? TvrageId { get; set; }

    [JsonPropertyName("ImdbID")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("InfoHash")]
    public string? InfoHash { get; set; }

    // v5-develop addition: used for subtitle/freeleech flag detection
    [JsonPropertyName("Tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("DownloadURL")]
    public string DownloadUrl { get; set; } = "";
}

/// <summary>
/// BroadcasTheNet (BTN) indexer client for Sportarr.
/// Implements JSON-RPC API for searching torrent releases.
/// Rate limit: 5 seconds between requests, 150 requests/hour max.
/// </summary>
public partial class BroadcasTheNetClient
{
    // Matches Sonarr's BroadcastheNet.RateLimit = TimeSpan.FromSeconds(5)
    private static readonly TimeSpan RateLimit = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly IRateLimitService _rateLimitService;
    private readonly ILogger<BroadcasTheNetClient> _logger;
    private readonly QualityDetectionService? _qualityDetection;

    public BroadcasTheNetClient(HttpClient httpClient, IRateLimitService rateLimitService, ILogger<BroadcasTheNetClient> logger, QualityDetectionService? qualityDetection = null)
    {
        _httpClient = httpClient;
        _rateLimitService = rateLimitService;
        _logger = logger;
        _qualityDetection = qualityDetection;
    }

    /// <summary>
    /// Test connection to BTN indexer
    /// </summary>
    public async Task<bool> TestConnectionAsync(Indexer config)
    {
        try
        {
            var query = new BroadcastheNetTorrentQuery { Age = "<=86400" };
            var request = BuildJsonRpcRequest(config, "getTorrents", query, 1);
            var response = await SendRequestAsync<JsonRpcResponse<BroadcastheNetTorrents>>(config, request);

            if (response.Error != null)
            {
                _logger.LogWarning("[BTN] Connection test failed for {Indexer}: {Error}", config.Name, response.Error.Message);
                return false;
            }

            _logger.LogInformation("[BTN] Connection successful to {Indexer}", config.Name);
            return true;
        }
        catch (IndexerRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogError(ex, "[BTN] Connection test failed for {Indexer}: Invalid API key", config.Name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BTN] Connection test failed for {Indexer}", config.Name);
            throw;
        }
    }

    /// <summary>
    /// Search for releases matching query using wildcard name search.
    /// Generates multiple search patterns to improve hit rate for sports content.
    /// </summary>
    public async Task<List<ReleaseSearchResult>> SearchAsync(Indexer config, string query, int maxResults = 100)
    {
        var searchPatterns = GenerateBtnSearchPatterns(query);
        if (searchPatterns.Count == 0)
        {
            _logger.LogDebug("[BTN] Empty query for {Indexer} — skipping search", config.Name);
            return new List<ReleaseSearchResult>();
        }

        var allResults = new List<ReleaseSearchResult>();
        var seenGuids = new HashSet<string>();

        _logger.LogInformation("[BTN] Searching {Indexer} for: {Query} ({PatternCount} patterns)", config.Name, query, searchPatterns.Count);

        foreach (var pattern in searchPatterns)
        {
            var searchQuery = new BroadcastheNetTorrentQuery
            {
                Name = pattern,
                Category = "Episode"
            };

            var request = BuildJsonRpcRequest(config, "getTorrents", searchQuery, Math.Min(maxResults / searchPatterns.Count + 10, 100));

            _logger.LogDebug("[BTN] Search pattern: {Pattern}", pattern);

            try
            {
                var response = await SendRequestAsync<JsonRpcResponse<BroadcastheNetTorrents>>(config, request);
                var results = ParseSearchResults(response, config);

                // Deduplicate by GUID
                foreach (var result in results)
                {
                    if (seenGuids.Add(result.Guid))
                    {
                        allResults.Add(result);
                    }
                }

                // If we got good results, we can stop early
                if (allResults.Count >= maxResults)
                {
                    break;
                }
            }
            catch (IndexerRateLimitException)
            {
                throw;
            }
            catch (IndexerRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Auth failure is terminal — no point trying remaining patterns.
                throw;
            }
            catch (Exception ex)
            {
                // Per-pattern API errors (e.g. overly-broad query) are non-terminal: log and try next pattern.
                _logger.LogWarning(ex, "[BTN] Search pattern failed for {Indexer}: {Pattern}", config.Name, pattern);
            }
        }

        _logger.LogInformation("[BTN] Found {Count} unique results from {Indexer}", allResults.Count, config.Name);
        return allResults.Take(maxResults).ToList();
    }

    /// <summary>
    /// Generate multiple search patterns for BTN wildcard search.
    /// Creates variations with different separators and formats to improve hit rate.
    /// </summary>
    private static List<string> GenerateBtnSearchPatterns(string query)
    {
        var patterns = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
            return patterns;

        // Normalize the query first
        var normalized = SearchNormalizationService.NormalizeForSearch(query);

        // Remove any existing wildcards to avoid "%%"
        normalized = normalized.Replace("%", "").Replace("_", "");

        // Pattern 1: Dot-separated (most common in scene releases)
        // "Formula 1 2026" -> "%Formula.1.2026%"
        var dotSeparated = Regex.Replace(normalized, @"[\s\-_]+", ".");
        patterns.Add($"%{dotSeparated}%");

        // Pattern 2: Single wildcard between words (more flexible)
        // "Formula 1 2026" -> "%Formula%1%2026%"
        var words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            var flexiblePattern = "%" + string.Join("%", words) + "%";
            patterns.Add(flexiblePattern);
        }

        // Pattern 3: First two words only (for broader matches)
        // Useful when event has many words but releases use shortened names
        if (words.Length >= 2)
        {
            var shortPattern = $"%{words[0]}.{words[1]}%";
            if (!patterns.Contains(shortPattern))
            {
                patterns.Add(shortPattern);
            }
        }

        // Pattern 4: For UFC events, add flexible wildcard variant (dot-sep already covered by Pattern 1)
        // "UFC 300" -> "%UFC%300%"
        var ufcMatch = Regex.Match(normalized, @"^UFC\s*(\d+)");
        if (ufcMatch.Success)
        {
            var ufcNumber = ufcMatch.Groups[1].Value;
            patterns.Add($"%UFC%{ufcNumber}%");
        }

        // Pattern 5: For F1/Formula 1 with year and round
        // "Formula 1 2026 Round 2" -> "%2026x02%" (common scene format)
        var f1Match = Regex.Match(normalized, @"20(\d{2})\s+(?:Round\s+|R)?(\d{1,2})");
        if (f1Match.Success)
        {
            var year = f1Match.Groups[1].Value;
            var round = f1Match.Groups[2].Value.PadLeft(2, '0');
            patterns.Add($"%20{year}x{round}%");
        }

        // Remove duplicates while preserving order
        return patterns.Distinct().ToList();
    }

    /// <summary>
    /// Fetch recent releases for RSS sync
    /// </summary>
    public async Task<List<ReleaseSearchResult>> FetchRecentAsync(Indexer config, int maxResults = 100)
    {
        var query = new BroadcastheNetTorrentQuery { Age = "<=86400" };

        var request = BuildJsonRpcRequest(config, "getTorrents", query, maxResults);

        _logger.LogDebug("[BTN] Fetching recent releases from {Indexer}", config.Name);

        try
        {
            var response = await SendRequestAsync<JsonRpcResponse<BroadcastheNetTorrents>>(config, request);
            return ParseSearchResults(response, config);
        }
        catch (IndexerRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BTN] RSS fetch failed for {Indexer}", config.Name);
            throw new IndexerRequestException($"RSS fetch failed for {config.Name}: {ex.Message}", HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// Build JSON-RPC request
    /// </summary>
    private JsonRpcRequest BuildJsonRpcRequest(Indexer config, string method, BroadcastheNetTorrentQuery query, int limit, int offset = 0)
    {
        return new JsonRpcRequest
        {
            Method = method,
            Params = new List<object>
            {
                config.ApiKey ?? "",  // API key as first parameter
                query,                // Search query object
                limit,                // Page size
                offset                // Page offset
            }
        };
    }

    /// <summary>
    /// Send HTTP request with error handling
    /// </summary>
    private async Task<T> SendRequestAsync<T>(Indexer config, JsonRpcRequest request)
    {
        var baseUrl = config.Url.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = "https://api.broadcasthe.net";

        // Rate limit every outbound call so multi-pattern searches don't fire back-to-back.
        // Use the hostname as the base key so all BTN indexers share the same
        // per-host bucket, matching how RateLimitHandler keys Torznab/Newznab.
        // Fall back to the raw URL if parsing fails (misconfigured Url field).
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;
        await _rateLimitService.WaitAndPulseAsync(host, config.Id.ToString(), RateLimit);

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/")
        {
            Content = content
        };

        // Sonarr sends both accept types; match exactly so BTN's content negotiation doesn't differ.
        requestMessage.Headers.Accept.ParseAdd("application/json-rpc, application/json");

        using var response = await _httpClient.SendAsync(requestMessage);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new IndexerRequestException($"BTN API key invalid for {config.Name}", HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IndexerRequestException($"BTN API endpoint not found for {config.Name} - API may have changed", HttpStatusCode.NotFound);

        // Redirect means Cloudflare or the site is intercepting (AllowAutoRedirect is disabled for BTN).
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString() ?? "(no Location header)";
            _logger.LogWarning("[BTN] Redirected for {Indexer}: {Status} → {Location}", config.Name, response.StatusCode, location);
            throw new IndexerRequestException(
                $"BTN API redirected ({response.StatusCode}) for {config.Name} - possible Cloudflare block. Location: {location}",
                HttpStatusCode.ServiceUnavailable);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Read body once and reuse for both the rate-limit check and generic error log.
            var errorBody = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                errorBody.Contains("Call Limit Exceeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new IndexerRateLimitException($"BTN rate limit exceeded for {config.Name} (150 requests/hour)", TimeSpan.FromMinutes(15));
            }
            _logger.LogWarning("[BTN] HTTP error {Status} for {Indexer}: {Body}", response.StatusCode, config.Name, errorBody);
            throw new IndexerRequestException($"BTN request failed: {response.StatusCode}", response.StatusCode);
        }

        // Sonarr pattern: HTML response means site is blocked/behind captcha
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var htmlSnippet = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("[BTN] HTML response from {Indexer} (HTTP {Status}): {Snippet}",
                config.Name, (int)response.StatusCode, htmlSnippet.Length > 500 ? htmlSnippet[..500] : htmlSnippet);
            throw new IndexerRequestException($"BTN returned HTML for {config.Name} - site may be blocked or behind a captcha", HttpStatusCode.ServiceUnavailable);
        }

        var responseJson = await response.Content.ReadAsStringAsync();

        // BTN sometimes returns plain-text errors with HTTP 200 (e.g. "Error: Invalid API Key")
        var trimmed = responseJson.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            var plainText = responseJson.Trim();
            if (plainText.Contains("Invalid API Key", StringComparison.OrdinalIgnoreCase) ||
                plainText.Contains("API Key", StringComparison.OrdinalIgnoreCase))
            {
                throw new IndexerRequestException($"BTN API key invalid for {config.Name}", HttpStatusCode.Unauthorized);
            }
            if (plainText.Contains("Call Limit Exceeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new IndexerRateLimitException($"BTN rate limit exceeded for {config.Name} (150 requests/hour)", TimeSpan.FromMinutes(15));
            }
            if (plainText.Equals("Query execution was interrupted", StringComparison.OrdinalIgnoreCase))
            {
                throw new IndexerRequestException(
                    $"BTN query interrupted for {config.Name} - query may be too broad",
                    HttpStatusCode.InternalServerError);
            }
            _logger.LogWarning("[BTN] Non-JSON response from {Indexer}: {Body}", config.Name, plainText);
            throw new IndexerRequestException($"BTN returned unexpected response for {config.Name}: {plainText}", HttpStatusCode.InternalServerError);
        }

        var result = JsonSerializer.Deserialize<T>(responseJson);
        if (result == null)
        {
            throw new IndexerRequestException($"Failed to parse BTN response for {config.Name}", HttpStatusCode.InternalServerError);
        }

        return result;
    }

    /// <summary>
    /// Parse BTN search results into ReleaseSearchResult objects
    /// </summary>
    private List<ReleaseSearchResult> ParseSearchResults(JsonRpcResponse<BroadcastheNetTorrents> response, Indexer config)
    {
        var results = new List<ReleaseSearchResult>();

        if (response.Error != null)
        {
            _logger.LogWarning("[BTN] API error for {Indexer}: {Error}", config.Name, response.Error.Message);
            throw new IndexerRequestException(
                $"BTN API error for {config.Name}: {response.Error.Message}",
                HttpStatusCode.InternalServerError);
        }

        if (response.Result == null || response.Result.Torrents == null || response.Result.Torrents.Count == 0)
        {
            _logger.LogDebug("[BTN] No results from {Indexer}", config.Name);
            return results;
        }

        foreach (var torrent in response.Result.Torrents.Values)
        {
            var releaseName = torrent.ReleaseName?.Replace("\\", "") ?? "";

            var result = new ReleaseSearchResult
            {
                Title = releaseName,
                Guid = $"BTN-{torrent.TorrentId}",
                DownloadUrl = torrent.DownloadUrl,
                InfoUrl = $"https://broadcasthe.net/torrents.php?id={torrent.GroupId}&torrentid={torrent.TorrentId}",
                Indexer = config.Name,
                TorrentInfoHash = torrent.InfoHash,
                PublishDate = DateTimeOffset.FromUnixTimeSeconds(torrent.Time).UtcDateTime,
                Size = torrent.Size,
                Seeders = torrent.Seeders,
                Leechers = torrent.Leechers,
                Language = LanguageDetector.DetectLanguage(releaseName),
                ReleaseGroup = ExtractReleaseGroup(releaseName)
            };

            // Parse quality using enhanced detection service if available
            if (_qualityDetection != null)
            {
                var qualityInfo = _qualityDetection.ParseQuality(releaseName);
                result.Quality = qualityInfo.Resolution ?? torrent.Resolution;
                result.Source = qualityInfo.Source ?? torrent.Source;
                result.Codec = qualityInfo.Codec ?? torrent.Codec;
            }
            else
            {
                // Fallback to BTN metadata
                result.Quality = torrent.Resolution;
                result.Source = torrent.Source;
                result.Codec = torrent.Codec;
            }

            // Calculate score based on seeders and quality
            result.Score = CalculateScore(result);

            results.Add(result);
        }

        _logger.LogInformation("[BTN] Found {Count} results from {Indexer}", results.Count, config.Name);
        return results;
    }

    /// <summary>
    /// Extract release group from title
    /// </summary>
    [GeneratedRegex(@"-([A-Za-z0-9]+)(?:\.[a-z]{2,4})?$")]
    private static partial Regex ReleaseGroupRegex();

    private static string? ExtractReleaseGroup(string title)
    {
        var match = ReleaseGroupRegex().Match(title);
        if (!match.Success) return null;
        var group = match.Groups[1].Value;
        var excluded = new[] { "DL", "WEB", "HD", "SD", "UHD" };
        return excluded.Contains(group.ToUpper()) ? null : group;
    }

    /// <summary>
    /// Calculate release score
    /// </summary>
    private int CalculateScore(ReleaseSearchResult result)
    {
        int score = 0;

        // Seeders are important for torrents
        if (result.Seeders.HasValue)
        {
            score += Math.Min(result.Seeders.Value * 10, 500);
        }

        // Quality bonus
        score += result.Quality?.ToLower() switch
        {
            "2160p" or "4k" => 100,
            "1080p" => 80,
            "720p" => 60,
            "480p" => 40,
            _ => 20
        };

        // Newer releases get bonus
        var age = DateTime.UtcNow - result.PublishDate;
        if (age.TotalDays < 7)
        {
            score += 50;
        }
        else if (age.TotalDays < 30)
        {
            score += 25;
        }

        return score;
    }
}
