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

    [JsonPropertyName("id")]
    public int Id { get; set; } = 1;
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
    public int Id { get; set; }
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
/// BTN torrent query parameters
/// </summary>
public class BroadcastheNetTorrentQuery
{
    [JsonPropertyName("tvdb")]
    public int? Tvdb { get; set; }

    [JsonPropertyName("tvrage")]
    public int? Tvrage { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("age")]
    public string? Age { get; set; }
}

/// <summary>
/// BTN torrents response container
/// </summary>
public class BroadcastheNetTorrents
{
    [JsonPropertyName("results")]
    public int Results { get; set; }

    [JsonPropertyName("torrents")]
    public Dictionary<string, BroadcastheNetTorrent>? Torrents { get; set; }
}

/// <summary>
/// Individual BTN torrent details
/// </summary>
public class BroadcastheNetTorrent
{
    [JsonPropertyName("TorrentID")]
    public string TorrentId { get; set; } = "";

    [JsonPropertyName("GroupID")]
    public string GroupId { get; set; } = "";

    [JsonPropertyName("ReleaseName")]
    public string ReleaseName { get; set; } = "";

    [JsonPropertyName("DownloadURL")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("InfoHash")]
    public string? InfoHash { get; set; }

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    [JsonPropertyName("Seeders")]
    public int Seeders { get; set; }

    [JsonPropertyName("Leechers")]
    public int Leechers { get; set; }

    [JsonPropertyName("Time")]
    public long Time { get; set; }

    [JsonPropertyName("TvdbID")]
    public string? TvdbId { get; set; }

    [JsonPropertyName("Origin")]
    public string? Origin { get; set; }

    [JsonPropertyName("Source")]
    public string? Source { get; set; }

    [JsonPropertyName("Container")]
    public string? Container { get; set; }

    [JsonPropertyName("Codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("Resolution")]
    public string? Resolution { get; set; }
}

/// <summary>
/// BroadcasTheNet (BTN) indexer client for Sportarr.
/// Implements JSON-RPC API for searching torrent releases.
/// Rate limit: 5 seconds between requests, 150 requests/hour max.
/// </summary>
public class BroadcasTheNetClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BroadcasTheNetClient> _logger;
    private readonly QualityDetectionService? _qualityDetection;

    // Rate limiting: 5 seconds between requests (per-instance, not global)
    private readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(5);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);

    public BroadcasTheNetClient(HttpClient httpClient, ILogger<BroadcasTheNetClient> logger, QualityDetectionService? qualityDetection = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _qualityDetection = qualityDetection;
    }

    /// <summary>
    /// Test connection to BTN indexer by calling getTorrents with empty query
    /// </summary>
    public async Task<bool> TestConnectionAsync(Indexer config)
    {
        try
        {
            var query = new BroadcastheNetTorrentQuery { Age = "<=86400" };
            var request = BuildJsonRpcRequest(config, "getTorrents", query, 1, 0);
            var response = await SendRequestAsync<JsonRpcResponse<BroadcastheNetTorrents>>(config, request);

            if (response.Error != null)
            {
                _logger.LogWarning("[BTN] Connection test failed for {Indexer}: {Error}", config.Name, response.Error.Message);
                return false;
            }

            _logger.LogInformation("[BTN] Connection successful to {Indexer}", config.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BTN] Connection test failed for {Indexer}", config.Name);
            return false;
        }
    }

    /// <summary>
    /// Search for releases matching query using wildcard name search.
    /// Generates multiple search patterns to improve hit rate for sports content.
    /// </summary>
    public async Task<List<ReleaseSearchResult>> SearchAsync(Indexer config, string query, int maxResults = 100)
    {
        // Apply rate limiting
        await ApplyRateLimitAsync();

        // Generate search variations with improved wildcard patterns
        var searchPatterns = GenerateBtnSearchPatterns(query);
        var allResults = new List<ReleaseSearchResult>();
        var seenGuids = new HashSet<string>();

        _logger.LogInformation("[BTN] Searching {Indexer} for: {Query} ({PatternCount} patterns)", config.Name, query, searchPatterns.Count);

        foreach (var pattern in searchPatterns)
        {
            var searchQuery = new BroadcastheNetTorrentQuery
            {
                Name = pattern,
                Age = "<=604800"  // Last 7 days for sports events
            };

            var request = BuildJsonRpcRequest(config, "getTorrents", searchQuery, maxResults / searchPatterns.Count + 10, 0);

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
            catch (IndexerRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BTN] Search pattern failed for {Indexer}: {Pattern}", config.Name, pattern);
                // Continue with next pattern
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
        {
            patterns.Add("%");
            return patterns;
        }

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

        // Pattern 4: For UFC/events with numbers, try specific patterns
        // "UFC 300" -> "%UFC.300%" and "%UFC%300%"
        var ufcMatch = Regex.Match(normalized, @"^(UFC)\s*(\d+)");
        if (ufcMatch.Success)
        {
            var ufcNumber = ufcMatch.Groups[2].Value;
            patterns.Add($"%UFC.{ufcNumber}%");
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
        // Apply rate limiting
        await ApplyRateLimitAsync();

        // Query for recent releases (last 24 hours)
        var query = new BroadcastheNetTorrentQuery
        {
            Age = "<=86400"  // Last 24 hours
        };

        var request = BuildJsonRpcRequest(config, "getTorrents", query, maxResults, 0);

        _logger.LogDebug("[BTN] Fetching recent releases from {Indexer}", config.Name);

        try
        {
            var response = await SendRequestAsync<JsonRpcResponse<BroadcastheNetTorrents>>(config, request);
            return ParseSearchResults(response, config);
        }
        catch (IndexerRateLimitException)
        {
            throw;
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
    private JsonRpcRequest BuildJsonRpcRequest(Indexer config, string method, BroadcastheNetTorrentQuery query, int limit, int offset)
    {
        return new JsonRpcRequest
        {
            Method = method,
            Params = new List<object>
            {
                config.ApiKey ?? "",  // API key as first parameter
                query,                // Search query object
                limit,                // Page size
                offset                // Offset for pagination
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
        {
            baseUrl = "https://api.broadcasthe.net";
        }

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/")
        {
            Content = content
        };

        // Add indexer ID header for rate limit tracking
        requestMessage.Headers.Add("X-Indexer-Id", config.Id.ToString());

        using var response = await _httpClient.SendAsync(requestMessage);

        // Handle specific HTTP errors
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new IndexerRequestException($"BTN API key invalid for {config.Name}", HttpStatusCode.Unauthorized);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new IndexerRequestException($"BTN API endpoint not found for {config.Name} - API may have changed", HttpStatusCode.NotFound);
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            if (responseBody.Contains("Call Limit Exceeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new IndexerRateLimitException($"BTN rate limit exceeded for {config.Name} (150 requests/hour)", TimeSpan.FromMinutes(15));
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("[BTN] HTTP error {Status} for {Indexer}: {Body}", response.StatusCode, config.Name, errorBody);
            throw new IndexerRequestException($"BTN request failed: {response.StatusCode}", response.StatusCode);
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
            _logger.LogWarning("[BTN] Non-JSON response from {Indexer}: {Body}", config.Name, plainText);
            throw new IndexerRequestException($"BTN returned unexpected response for {config.Name}: {plainText}", HttpStatusCode.InternalServerError);
        }

        // Check for query execution error
        if (responseJson.Contains("Query execution was interrupted", StringComparison.OrdinalIgnoreCase))
        {
            throw new IndexerRequestException($"BTN server error for {config.Name}: Query execution was interrupted", HttpStatusCode.InternalServerError);
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
            return results;
        }

        if (response.Result == null || response.Result.Torrents == null || response.Result.Torrents.Count == 0)
        {
            _logger.LogDebug("[BTN] No results from {Indexer}", config.Name);
            return results;
        }

        foreach (var torrent in response.Result.Torrents.Values)
        {
            var result = new ReleaseSearchResult
            {
                Title = torrent.ReleaseName,
                Guid = $"BTN-{torrent.TorrentId}",
                DownloadUrl = torrent.DownloadUrl,
                InfoUrl = $"https://broadcasthe.net/torrents.php?id={torrent.GroupId}&torrentid={torrent.TorrentId}",
                Indexer = config.Name,
                TorrentInfoHash = torrent.InfoHash,
                PublishDate = DateTimeOffset.FromUnixTimeSeconds(torrent.Time).UtcDateTime,
                Size = torrent.Size,
                Seeders = torrent.Seeders,
                Leechers = torrent.Leechers,
                Language = LanguageDetector.DetectLanguage(torrent.ReleaseName),
                ReleaseGroup = ExtractReleaseGroup(torrent.ReleaseName)
            };

            // Parse quality using enhanced detection service if available
            if (_qualityDetection != null)
            {
                var qualityInfo = _qualityDetection.ParseQuality(torrent.ReleaseName);
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
    /// Apply rate limiting delay using async-compatible SemaphoreSlim
    /// </summary>
    private async Task ApplyRateLimitAsync()
    {
        await _rateLimitSemaphore.WaitAsync();
        try
        {
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest < RateLimitDelay)
            {
                var delay = RateLimitDelay - timeSinceLastRequest;
                _logger.LogDebug("[BTN] Rate limiting: waiting {DelayMs}ms", delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    /// <summary>
    /// Extract release group from title
    /// </summary>
    private static string? ExtractReleaseGroup(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title, @"-([A-Za-z0-9]+)(?:\.[a-z]{2,4})?$");
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

    /// <summary>
    /// Dispose resources (SemaphoreSlim)
    /// </summary>
    public void Dispose()
    {
        _rateLimitSemaphore?.Dispose();
    }
}
