using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Sportarr.Api.Services;

/// <summary>
/// Service for managing the local release cache.
///
/// This is the core of the RSS-first search strategy:
/// - RSS feeds are polled periodically and releases cached here
/// - When searching for an event, we query the local cache first (instant, no API calls)
/// - Active indexer search only happens as a fallback
///
/// Benefits:
/// - 1 API call per indexer per RSS sync vs N calls per event search
/// - All fuzzy matching happens locally with no rate limits
/// - Releases are discovered as they appear, not when you search
/// </summary>
public class ReleaseCacheService
{
    private readonly SportarrDbContext _db;
    private readonly ILogger<ReleaseCacheService> _logger;

    // Default cache expiration: 7 days for sports content
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromDays(7);

    public ReleaseCacheService(
        SportarrDbContext db,
        ILogger<ReleaseCacheService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Add or update releases in the cache from RSS sync or search results.
    /// Deduplicates by GUID to avoid storing the same release multiple times.
    /// </summary>
    public async Task<int> CacheReleasesAsync(
        IEnumerable<ReleaseSearchResult> releases,
        bool fromRss,
        CancellationToken cancellationToken = default)
    {
        var addedCount = 0;
        var updatedCount = 0;

        foreach (var release in releases)
        {
            try
            {
                // Check if release already exists in cache
                var existing = await _db.ReleaseCache
                    .FirstOrDefaultAsync(r => r.Guid == release.Guid, cancellationToken);

                if (existing != null)
                {
                    // Update existing entry (refresh TTL and update seeder count)
                    existing.Seeders = release.Seeders;
                    existing.Leechers = release.Leechers;
                    existing.ExpiresAt = DateTime.UtcNow.Add(DefaultCacheTtl);
                    updatedCount++;
                    continue;
                }

                // Parse and extract metadata from title
                var parsed = ParseReleaseTitle(release.Title);

                // Build searchable terms with expanded aliases
                var searchTerms = BuildSearchTerms(release.Title, parsed);

                var cacheEntry = new ReleaseCache
                {
                    Title = release.Title,
                    NormalizedTitle = NormalizeTitle(release.Title),
                    SearchTerms = searchTerms,
                    Guid = release.Guid,
                    DownloadUrl = release.DownloadUrl,
                    InfoUrl = release.InfoUrl,
                    Indexer = release.Indexer,
                    Protocol = release.Protocol,
                    TorrentInfoHash = release.TorrentInfoHash,
                    Size = release.Size,
                    Quality = release.Quality,
                    Source = release.Source,
                    Codec = release.Codec,
                    Language = release.Language,
                    Seeders = release.Seeders,
                    Leechers = release.Leechers,
                    PublishDate = release.PublishDate,
                    IndexerFlags = release.IndexerFlags,
                    CachedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(DefaultCacheTtl),
                    FromRss = fromRss,
                    Year = parsed.Year,
                    Month = parsed.Month,
                    Day = parsed.Day,
                    RoundNumber = parsed.RoundNumber,
                    SportPrefix = parsed.SportPrefix,
                    IsPack = parsed.IsPack
                };

                _db.ReleaseCache.Add(cacheEntry);
                addedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ReleaseCache] Error caching release: {Title}", release.Title);
            }
        }

        if (addedCount > 0 || updatedCount > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("[ReleaseCache] Cached {Added} new, {Updated} updated releases", addedCount, updatedCount);
        }

        return addedCount;
    }

    /// <summary>
    /// Query the local cache for releases matching an event.
    /// Uses smart normalization and alias expansion for fuzzy matching.
    /// </summary>
    public async Task<List<ReleaseSearchResult>> FindMatchingReleasesAsync(
        Event evt,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ReleaseSearchResult>();

        // Build search terms from event
        var eventSearchTerms = BuildEventSearchTerms(evt);

        _logger.LogDebug("[ReleaseCache] Searching for event '{Event}' with terms: {Terms}",
            evt.Title, string.Join(", ", eventSearchTerms.Take(5)));

        // Query cache using the search terms
        // We use a combination of indexed lookups and LIKE queries
        var query = _db.ReleaseCache
            .Where(r => r.ExpiresAt > DateTime.UtcNow); // Only non-expired entries

        // If we have year/round info, use indexed lookup first (fast)
        if (evt.EventDate.Year > 0)
        {
            query = query.Where(r => r.Year == null || r.Year == evt.EventDate.Year);
        }

        // Get potential matches based on sport prefix
        var sportPrefix = GetSportPrefix(evt.League?.Name, evt.Sport);
        if (!string.IsNullOrEmpty(sportPrefix))
        {
            query = query.Where(r => r.SportPrefix == null || r.SportPrefix == sportPrefix);
        }

        // Load candidates and do full matching in memory
        // (SQLite doesn't support complex text search well, so we load and filter)
        var candidates = await query
            .OrderByDescending(r => r.PublishDate)
            .Take(1000) // Limit to prevent memory issues
            .ToListAsync(cancellationToken);

        _logger.LogDebug("[ReleaseCache] Found {Count} candidate releases for filtering", candidates.Count);

        // Filter using the smart matching logic
        foreach (var cached in candidates)
        {
            if (IsMatch(cached, eventSearchTerms, evt))
            {
                results.Add(ToReleaseSearchResult(cached));
            }
        }

        _logger.LogDebug("[ReleaseCache] Matched {Count} releases for event '{Event}'",
            results.Count, evt.Title);

        return results;
    }

    /// <summary>
    /// Find releases matching a broad query (used for single-query search strategy).
    /// Example: "Formula1 2025" returns all F1 2025 releases.
    /// </summary>
    public async Task<List<ReleaseSearchResult>> FindByBroadQueryAsync(
        string broadQuery,
        int maxResults = 500,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeTitle(broadQuery);
        var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var candidates = await _db.ReleaseCache
            .Where(r => r.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(r => r.PublishDate)
            .Take(maxResults * 2) // Get extra for filtering
            .ToListAsync(cancellationToken);

        var results = candidates
            .Where(c => queryTerms.All(term =>
                c.NormalizedTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.SearchTerms.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(maxResults)
            .Select(ToReleaseSearchResult)
            .ToList();

        return results;
    }

    /// <summary>
    /// Clean up expired cache entries.
    /// Should be called periodically (e.g., daily).
    /// </summary>
    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var expiredCount = await _db.ReleaseCache
            .Where(r => r.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);

        if (expiredCount > 0)
        {
            _logger.LogInformation("[ReleaseCache] Cleaned up {Count} expired cache entries", expiredCount);
        }

        return expiredCount;
    }

    /// <summary>
    /// Get cache statistics for monitoring.
    /// </summary>
    public async Task<ReleaseCacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var totalCount = await _db.ReleaseCache.CountAsync(cancellationToken);
        var activeCount = await _db.ReleaseCache.CountAsync(r => r.ExpiresAt > now, cancellationToken);
        var rssCount = await _db.ReleaseCache.CountAsync(r => r.FromRss && r.ExpiresAt > now, cancellationToken);

        var oldestEntry = await _db.ReleaseCache
            .Where(r => r.ExpiresAt > now)
            .OrderBy(r => r.CachedAt)
            .Select(r => r.CachedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var newestEntry = await _db.ReleaseCache
            .Where(r => r.ExpiresAt > now)
            .OrderByDescending(r => r.CachedAt)
            .Select(r => r.CachedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var indexerCounts = await _db.ReleaseCache
            .Where(r => r.ExpiresAt > now)
            .GroupBy(r => r.Indexer)
            .Select(g => new { Indexer = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Indexer, x => x.Count, cancellationToken);

        return new ReleaseCacheStats
        {
            TotalEntries = totalCount,
            ActiveEntries = activeCount,
            RssEntries = rssCount,
            SearchEntries = activeCount - rssCount,
            OldestEntry = oldestEntry,
            NewestEntry = newestEntry,
            EntriesByIndexer = indexerCounts
        };
    }

    #region Private Helper Methods

    /// <summary>
    /// Normalize a release title for consistent matching.
    /// </summary>
    private string NormalizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";

        // Remove diacritics
        var normalized = SearchNormalizationService.RemoveDiacritics(title);

        // Replace common separators with spaces
        normalized = Regex.Replace(normalized, @"[\.\-_]", " ");

        // Collapse multiple spaces
        normalized = Regex.Replace(normalized, @"\s+", " ");

        return normalized.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Parse a release title to extract structured metadata.
    /// </summary>
    private ParsedRelease ParseReleaseTitle(string title)
    {
        var parsed = new ParsedRelease();

        // Extract year (4 digits, 2020+)
        var yearMatch = Regex.Match(title, @"\b(20[2-9]\d)\b");
        if (yearMatch.Success)
            parsed.Year = int.Parse(yearMatch.Groups[1].Value);

        // Extract round/week number
        var roundMatch = Regex.Match(title, @"(?:Round|R|Week|W)\.?(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (roundMatch.Success)
            parsed.RoundNumber = int.Parse(roundMatch.Groups[1].Value);

        // Extract date (YYYY.MM.DD or YYYY-MM-DD)
        var dateMatch = Regex.Match(title, @"\b(20[2-9]\d)[.\-](\d{2})[.\-](\d{2})\b");
        if (dateMatch.Success)
        {
            parsed.Year = int.Parse(dateMatch.Groups[1].Value);
            parsed.Month = int.Parse(dateMatch.Groups[2].Value);
            parsed.Day = int.Parse(dateMatch.Groups[3].Value);
        }

        // Detect sport prefix
        parsed.SportPrefix = DetectSportPrefix(title);

        // Detect pack releases (e.g., NFL-2025-Week15)
        parsed.IsPack = Regex.IsMatch(title, @"(?:Week|Round)\d+(?!.*(?:vs|@|\.v\.))", RegexOptions.IgnoreCase) &&
                       !Regex.IsMatch(title, @"(?:vs|@|\.v\.)", RegexOptions.IgnoreCase);

        return parsed;
    }

    /// <summary>
    /// Detect the sport/league prefix from a title.
    /// </summary>
    private string? DetectSportPrefix(string title)
    {
        var normalized = title.ToUpperInvariant();

        // Common motorsport prefixes
        if (normalized.Contains("FORMULA1") || normalized.Contains("FORMULA.1") || normalized.Contains("F1."))
            return "Formula1";
        if (normalized.Contains("MOTOGP") || normalized.Contains("MOTO.GP"))
            return "MotoGP";
        if (normalized.Contains("INDYCAR"))
            return "IndyCar";
        if (normalized.Contains("NASCAR"))
            return "NASCAR";
        if (normalized.Contains("WEC") || normalized.Contains("WORLD.ENDURANCE"))
            return "WEC";

        // Fighting sports
        if (normalized.Contains("UFC"))
            return "UFC";
        if (normalized.Contains("BELLATOR"))
            return "Bellator";
        if (normalized.Contains("PFL"))
            return "PFL";
        if (normalized.Contains("BOXING") || normalized.Contains("DAZN"))
            return "Boxing";
        if (normalized.Contains("WWE"))
            return "WWE";

        // Team sports
        if (normalized.Contains("NFL") && !normalized.Contains("UEFA"))
            return "NFL";
        if (normalized.Contains("NBA"))
            return "NBA";
        if (normalized.Contains("NHL"))
            return "NHL";
        if (normalized.Contains("MLB"))
            return "MLB";
        if (normalized.Contains("MLS"))
            return "MLS";
        if (normalized.Contains("EPL") || normalized.Contains("PREMIER.LEAGUE"))
            return "EPL";
        if (normalized.Contains("CHAMPIONS.LEAGUE") || normalized.Contains("UCL"))
            return "UCL";
        if (normalized.Contains("LA.LIGA") || normalized.Contains("LALIGA"))
            return "LaLiga";

        return null;
    }

    /// <summary>
    /// Build searchable terms from a release title, including expanded aliases.
    /// </summary>
    private string BuildSearchTerms(string title, ParsedRelease parsed)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add normalized words from title
        var normalized = NormalizeTitle(title);
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 1)
                terms.Add(word);
        }

        // Add expanded location aliases
        var variations = SearchNormalizationService.GenerateSearchVariations(title);
        foreach (var variation in variations)
        {
            var variationNormalized = NormalizeTitle(variation);
            foreach (var word in variationNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 1)
                    terms.Add(word);
            }
        }

        // Add sport prefix variations
        if (!string.IsNullOrEmpty(parsed.SportPrefix))
        {
            terms.Add(parsed.SportPrefix.ToLowerInvariant());
            // Add common variations
            if (parsed.SportPrefix == "Formula1")
            {
                terms.Add("f1");
                terms.Add("formula");
            }
        }

        return string.Join(" ", terms);
    }

    /// <summary>
    /// Build search terms from an event for matching against cache.
    /// </summary>
    private List<string> BuildEventSearchTerms(Event evt)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add words from event title
        var titleWords = NormalizeTitle(evt.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in titleWords)
        {
            if (word.Length > 1 && !IsCommonWord(word))
                terms.Add(word);
        }

        // Add league name
        if (!string.IsNullOrEmpty(evt.League?.Name))
        {
            var leagueWords = NormalizeTitle(evt.League.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in leagueWords)
            {
                if (word.Length > 1)
                    terms.Add(word);
            }
        }

        // Add year
        terms.Add(evt.EventDate.Year.ToString());

        // Add round if available
        if (!string.IsNullOrEmpty(evt.Round))
        {
            var roundMatch = Regex.Match(evt.Round, @"(\d+)");
            if (roundMatch.Success)
                terms.Add($"round{roundMatch.Groups[1].Value}");
        }

        // Add team names for team sports
        if (!string.IsNullOrEmpty(evt.HomeTeamName))
        {
            foreach (var word in NormalizeTitle(evt.HomeTeamName).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 2)
                    terms.Add(word);
            }
        }
        if (!string.IsNullOrEmpty(evt.AwayTeamName))
        {
            foreach (var word in NormalizeTitle(evt.AwayTeamName).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 2)
                    terms.Add(word);
            }
        }

        // Add variations from SearchNormalizationService
        var eventVariations = SearchNormalizationService.GenerateSearchVariations(evt.Title);
        foreach (var variation in eventVariations)
        {
            foreach (var word in NormalizeTitle(variation).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 1 && !IsCommonWord(word))
                    terms.Add(word);
            }
        }

        return terms.ToList();
    }

    /// <summary>
    /// Check if a cached release matches the event search terms.
    /// </summary>
    private bool IsMatch(ReleaseCache cached, List<string> searchTerms, Event evt)
    {
        // Check year match
        if (cached.Year.HasValue && cached.Year != evt.EventDate.Year)
            return false;

        // Use SearchNormalizationService for intelligent matching
        if (SearchNormalizationService.IsReleaseMatch(cached.Title, evt.Title))
            return true;

        // Fallback: check if enough search terms match
        var matchedTerms = 0;
        var requiredTerms = Math.Max(2, searchTerms.Count / 3); // At least 1/3 of terms must match

        foreach (var term in searchTerms)
        {
            if (cached.NormalizedTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                cached.SearchTerms.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matchedTerms++;
            }
        }

        return matchedTerms >= requiredTerms;
    }

    /// <summary>
    /// Get the sport prefix for an event.
    /// </summary>
    private string? GetSportPrefix(string? leagueName, string? sport)
    {
        if (!string.IsNullOrEmpty(leagueName))
        {
            var upper = leagueName.ToUpperInvariant();
            if (upper.Contains("FORMULA 1") || upper.Contains("F1"))
                return "Formula1";
            if (upper.Contains("UFC"))
                return "UFC";
            if (upper.Contains("NFL"))
                return "NFL";
            // Add more mappings as needed
        }

        return DetectSportPrefix(sport ?? "");
    }

    /// <summary>
    /// Check if a word is a common word that shouldn't be used for matching.
    /// </summary>
    private bool IsCommonWord(string word)
    {
        var commonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "at", "in", "on", "for", "to", "and", "or",
            "vs", "versus", "grand", "prix", "race", "match", "game", "event"
        };
        return commonWords.Contains(word);
    }

    /// <summary>
    /// Convert a cache entry back to a ReleaseSearchResult.
    /// </summary>
    private ReleaseSearchResult ToReleaseSearchResult(ReleaseCache cached)
    {
        return new ReleaseSearchResult
        {
            Title = cached.Title,
            Guid = cached.Guid,
            DownloadUrl = cached.DownloadUrl,
            InfoUrl = cached.InfoUrl,
            Indexer = cached.Indexer,
            Protocol = cached.Protocol,
            TorrentInfoHash = cached.TorrentInfoHash,
            Size = cached.Size,
            Quality = cached.Quality,
            Source = cached.Source,
            Codec = cached.Codec,
            Language = cached.Language,
            Seeders = cached.Seeders,
            Leechers = cached.Leechers,
            PublishDate = cached.PublishDate,
            IndexerFlags = cached.IndexerFlags,
            IsPack = cached.IsPack
        };
    }

    #endregion

    /// <summary>
    /// Parsed release metadata from title.
    /// </summary>
    private class ParsedRelease
    {
        public int? Year { get; set; }
        public int? Month { get; set; }
        public int? Day { get; set; }
        public int? RoundNumber { get; set; }
        public string? SportPrefix { get; set; }
        public bool IsPack { get; set; }
    }
}

/// <summary>
/// Statistics about the release cache.
/// </summary>
public class ReleaseCacheStats
{
    public int TotalEntries { get; set; }
    public int ActiveEntries { get; set; }
    public int RssEntries { get; set; }
    public int SearchEntries { get; set; }
    public DateTime OldestEntry { get; set; }
    public DateTime NewestEntry { get; set; }
    public Dictionary<string, int> EntriesByIndexer { get; set; } = new();
}
