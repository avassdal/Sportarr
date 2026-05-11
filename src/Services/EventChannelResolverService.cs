using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// Resolves a sports Event to the IPTV channels that are airing it,
/// using two layered signals:
///
///   1. Event.Broadcast - already populated from the metadata API's
///      /lookup/event_tv endpoint (e.g. "ESPN / ABC", "Sky Sports Main
///      Event", "TNT Sports 1"). Authoritative for which network
///      holds the rights.
///   2. The user's IPTV channel inventory (tvg-id, name, country).
///
/// Resolution = tokenize the broadcast string, fuzzy-match each token
/// against every enabled IPTV channel, score each candidate, and
/// return them sorted high-to-low. Callers (DvrAutoSchedulerService,
/// EventDvrService) decide what confidence threshold makes them
/// schedule a recording.
///
/// Confidence is on a 0-100 scale, blended from:
///   - Token-set fuzzy ratio (FuzzySharp.Fuzz.TokenSetRatio)
///   - +5 if broadcast token == channel.DetectedNetwork exactly
///   - +5 if quality tier matches user's preference
///   - -10 if the broadcast string is empty (we have nothing to match)
///   - +10 if there's already a ChannelLeagueMapping marking this
///     channel as preferred for the event's league
/// </summary>
public class EventChannelResolverService
{
    private readonly SportarrDbContext _db;
    private readonly ILogger<EventChannelResolverService> _logger;

    private const int MinAcceptableConfidence = 65;
    private const int HighConfidence = 85;

    public EventChannelResolverService(SportarrDbContext db, ILogger<EventChannelResolverService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Resolve channels for a single event, returning candidates
    /// sorted by confidence descending. Empty list when the event
    /// has no broadcast data and no league mapping.
    /// </summary>
    public async Task<List<EventChannelCandidate>> ResolveAsync(int eventId, CancellationToken ct = default)
    {
        var evt = await _db.Events
            .Include(e => e.League)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (evt == null) return new List<EventChannelCandidate>();

        var leagueId = evt.LeagueId ?? 0;

        var channels = await _db.IptvChannels
            .Include(c => c.Source)
            .Where(c => c.IsEnabled && c.Source != null && c.Source.IsActive)
            .ToListAsync(ct);
        if (channels.Count == 0) return new List<EventChannelCandidate>();

        // Pull existing league mappings so we can boost preferred
        // channels and fall back to them when broadcast data is empty.
        // leagueId == 0 means the event has no league - the query
        // returns no mappings and we fall through to broadcast-only.
        var leagueMappings = leagueId > 0
            ? await _db.ChannelLeagueMappings
                .Where(m => m.LeagueId == leagueId)
                .ToListAsync(ct)
            : new List<ChannelLeagueMapping>();
        var preferredChannelIds = new HashSet<int>(leagueMappings.Where(m => m.IsPreferred).Select(m => m.ChannelId));
        var mappedChannelIds = new HashSet<int>(leagueMappings.Select(m => m.ChannelId));

        var broadcastTokens = TokenizeBroadcast(evt.Broadcast);

        var candidates = new List<EventChannelCandidate>();
        foreach (var ch in channels)
        {
            int score = 0;
            string source;

            if (broadcastTokens.Count > 0)
            {
                score = ScoreAgainstBroadcast(ch, broadcastTokens);
                source = "broadcast";
            }
            else if (mappedChannelIds.Contains(ch.Id))
            {
                // No broadcast data, but the channel is mapped to the
                // league - fall back to that signal at a moderate
                // confidence so we still schedule something.
                score = preferredChannelIds.Contains(ch.Id) ? 80 : 70;
                source = "league-mapping";
            }
            else
            {
                continue;
            }

            // Boost preferred channels for this league regardless of
            // which path we took.
            if (preferredChannelIds.Contains(ch.Id)) score += 10;
            else if (mappedChannelIds.Contains(ch.Id)) score += 5;

            // Country/region hint - if the event's league has a
            // country and the channel's country matches, boost.
            if (!string.IsNullOrEmpty(evt.League?.Country) &&
                !string.IsNullOrEmpty(ch.Country) &&
                string.Equals(evt.League.Country, ch.Country, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }

            score = Math.Clamp(score, 0, 100);
            if (score < MinAcceptableConfidence) continue;

            candidates.Add(new EventChannelCandidate(
                ch.Id,
                ch.Name,
                ch.Source?.Name ?? "(unknown)",
                ch.QualityScore,
                ch.DetectedQuality,
                score,
                source));
        }

        return candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.QualityScore)
            .ToList();
    }

    /// <summary>
    /// Best single channel for an event, or null if nothing scores
    /// at or above the high-confidence threshold. Used by the
    /// auto-scheduler when it wants to make an unattended decision.
    /// </summary>
    public async Task<EventChannelCandidate?> BestMatchAsync(int eventId, CancellationToken ct = default)
    {
        var ranked = await ResolveAsync(eventId, ct);
        var top = ranked.FirstOrDefault();
        if (top == null) return null;
        return top.Confidence >= HighConfidence ? top : null;
    }

    /// <summary>
    /// Bulk variant for the daily auto-scheduler sweep. Avoids
    /// re-loading the channel list per event.
    /// </summary>
    public async Task<Dictionary<int, EventChannelCandidate>> ResolveManyAsync(
        IEnumerable<int> eventIds,
        CancellationToken ct = default)
    {
        var ids = eventIds.ToList();
        if (ids.Count == 0) return new();

        var events = await _db.Events
            .Include(e => e.League)
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);

        var channels = await _db.IptvChannels
            .Include(c => c.Source)
            .Where(c => c.IsEnabled && c.Source != null && c.Source.IsActive)
            .ToListAsync(ct);

        var leagueIds = events.Select(e => e.LeagueId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var mappings = await _db.ChannelLeagueMappings
            .Where(m => leagueIds.Contains(m.LeagueId))
            .ToListAsync(ct);

        var byLeagueMapped = mappings.GroupBy(m => m.LeagueId)
            .ToDictionary(g => g.Key, g => new HashSet<int>(g.Select(m => m.ChannelId)));
        var byLeaguePreferred = mappings.Where(m => m.IsPreferred).GroupBy(m => m.LeagueId)
            .ToDictionary(g => g.Key, g => new HashSet<int>(g.Select(m => m.ChannelId)));

        var result = new Dictionary<int, EventChannelCandidate>();
        foreach (var evt in events)
        {
            // Events without a league won't have league mappings to
            // fall back on, but their broadcast string may still be
            // resolvable. Treat the missing league as "no mapped or
            // preferred channels".
            var leagueId = evt.LeagueId ?? -1;
            var broadcastTokens = TokenizeBroadcast(evt.Broadcast);
            var preferred = byLeaguePreferred.TryGetValue(leagueId, out var p) ? p : new HashSet<int>();
            var mapped = byLeagueMapped.TryGetValue(leagueId, out var m) ? m : new HashSet<int>();

            EventChannelCandidate? best = null;
            foreach (var ch in channels)
            {
                int score;
                string source;
                if (broadcastTokens.Count > 0)
                {
                    score = ScoreAgainstBroadcast(ch, broadcastTokens);
                    source = "broadcast";
                }
                else if (mapped.Contains(ch.Id))
                {
                    score = preferred.Contains(ch.Id) ? 80 : 70;
                    source = "league-mapping";
                }
                else continue;

                if (preferred.Contains(ch.Id)) score += 10;
                else if (mapped.Contains(ch.Id)) score += 5;

                if (!string.IsNullOrEmpty(evt.League?.Country) &&
                    !string.IsNullOrEmpty(ch.Country) &&
                    string.Equals(evt.League.Country, ch.Country, StringComparison.OrdinalIgnoreCase))
                    score += 5;

                score = Math.Clamp(score, 0, 100);
                if (score < HighConfidence) continue;

                if (best == null
                    || score > best.Confidence
                    || (score == best.Confidence && (ch.QualityScore) > best.QualityScore))
                {
                    best = new EventChannelCandidate(
                        ch.Id, ch.Name, ch.Source?.Name ?? "(unknown)",
                        ch.QualityScore, ch.DetectedQuality, score, source);
                }
            }

            if (best != null) result[evt.Id] = best;
        }

        return result;
    }

    private static List<string> TokenizeBroadcast(string? broadcast)
    {
        if (string.IsNullOrWhiteSpace(broadcast)) return new List<string>();

        // Broadcast strings come from BuildBroadcastString as
        // "Network / Channel / StreamingService". Split on / and on
        // commas so multi-network broadcasts like "ESPN, ABC" also
        // produce two tokens.
        return broadcast
            .Split(new[] { '/', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 1)
            .ToList();
    }

    private static int ScoreAgainstBroadcast(IptvChannel channel, List<string> broadcastTokens)
    {
        int best = 0;
        var channelName = channel.Name ?? string.Empty;
        var tvgName = channel.TvgName ?? string.Empty;
        var detectedNetwork = channel.DetectedNetwork ?? string.Empty;

        foreach (var token in broadcastTokens)
        {
            // TokenSetRatio is order-insensitive: "Sky Sports Main Event"
            // vs "Main Event Sky Sports" still scores high. That's
            // exactly the variability we get between metadata APIs
            // and IPTV providers.
            var nameScore = Fuzz.TokenSetRatio(token, channelName);
            var tvgScore = string.IsNullOrEmpty(tvgName) ? 0 : Fuzz.TokenSetRatio(token, tvgName);
            var netScore = string.IsNullOrEmpty(detectedNetwork) ? 0 : Fuzz.TokenSetRatio(token, detectedNetwork);

            var blended = Math.Max(nameScore, Math.Max(tvgScore, netScore));

            // Exact network hit gets a small bonus on top of the
            // raw fuzzy score.
            if (!string.IsNullOrEmpty(detectedNetwork) &&
                string.Equals(token, detectedNetwork, StringComparison.OrdinalIgnoreCase))
                blended = Math.Min(100, blended + 5);

            if (blended > best) best = blended;
        }

        return best;
    }
}

/// <summary>
/// One candidate channel for an event. Confidence is 0-100. Source
/// describes which signal contributed: "broadcast", "league-mapping".
/// </summary>
public record EventChannelCandidate(
    int ChannelId,
    string ChannelName,
    string SourceName,
    int QualityScore,
    string? DetectedQuality,
    int Confidence,
    string Source);
