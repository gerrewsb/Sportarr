using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// Service for managing scene mappings - syncs from Sportarr-API and provides local overrides.
/// Scene mappings help match release naming patterns to official database names.
/// Similar to TheXEM for Sonarr.
/// </summary>
public class SceneMappingService
{
    private readonly SportarrDbContext _db;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SceneMappingService> _logger;
    private readonly string _apiBaseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SceneMappingService(
        SportarrDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<SceneMappingService> logger,
        IConfiguration configuration)
    {
        _db = db;
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        // Use the same base as TheSportsDB but different endpoint path
        var baseUrl = configuration["TheSportsDB:ApiBaseUrl"] ?? "https://sportarr.net/api/v2/json";
        // Scene mappings are at /api/scene-mappings (not under /api/v2/json)
        _apiBaseUrl = baseUrl.Replace("/api/v2/json", "/api");
    }

    /// <summary>
    /// Sync scene mappings from the central Sportarr-API.
    /// Uses incremental sync when possible (only fetches updates since last sync).
    /// </summary>
    public async Task<SceneMappingSyncResult> SyncFromApiAsync(bool fullSync = false)
    {
        var result = new SceneMappingSyncResult();
        var startTime = DateTime.UtcNow;

        try
        {
            _logger.LogInformation("[SceneMapping] Starting sync from API (fullSync: {FullSync})", fullSync);

            // Get last sync time for incremental updates
            DateTime? lastSync = null;
            if (!fullSync)
            {
                lastSync = await _db.SceneMappings
                    .Where(m => m.Source != "local")
                    .MaxAsync(m => (DateTime?)m.LastSyncedAt);
            }

            // Fetch mappings from API
            var url = $"{_apiBaseUrl}/scene-mappings";
            if (lastSync.HasValue)
            {
                url += $"?since={lastSync.Value:O}";
            }

            _logger.LogDebug("[SceneMapping] Fetching from: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[SceneMapping] API returned {StatusCode}", response.StatusCode);
                result.Errors.Add($"API returned {response.StatusCode}");
                return result;
            }

            var json = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<SceneMappingApiResponse>(json, JsonOptions);

            if (apiResponse?.Mappings == null)
            {
                _logger.LogWarning("[SceneMapping] API returned null mappings");
                result.Errors.Add("API returned null mappings");
                return result;
            }

            _logger.LogInformation("[SceneMapping] Received {Count} mappings from API", apiResponse.Mappings.Count);

            // Process each mapping
            foreach (var remote in apiResponse.Mappings)
            {
                try
                {
                    await UpsertMappingAsync(remote, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SceneMapping] Error processing mapping {Id}", remote.Id);
                    result.Errors.Add($"Error processing mapping {remote.Id}: {ex.Message}");
                }
            }

            await _db.SaveChangesAsync();

            result.Duration = DateTime.UtcNow - startTime;
            result.Success = result.Errors.Count == 0;

            _logger.LogInformation(
                "[SceneMapping] Sync complete: {Added} added, {Updated} updated, {Unchanged} unchanged in {Duration:F1}s",
                result.Added, result.Updated, result.Unchanged, result.Duration.TotalSeconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SceneMapping] Sync failed");
            result.Errors.Add($"Sync failed: {ex.Message}");
            result.Duration = DateTime.UtcNow - startTime;
            return result;
        }
    }

    private async Task UpsertMappingAsync(SceneMappingDto remote, SceneMappingSyncResult result)
    {
        // Find existing mapping by remote ID
        var existing = await _db.SceneMappings
            .FirstOrDefaultAsync(m => m.RemoteId == remote.Id);

        if (existing == null)
        {
            // Check for conflict with sport/league combination
            existing = await _db.SceneMappings
                .FirstOrDefaultAsync(m => m.SportType == remote.SportType && m.LeagueId == remote.LeagueId);
        }

        if (existing == null)
        {
            // Create new mapping
            var mapping = new SceneMapping
            {
                RemoteId = remote.Id,
                SportType = remote.SportType,
                LeagueId = remote.LeagueId,
                LeagueName = remote.LeagueName,
                SceneNames = remote.SceneNames ?? new List<string>(),
                SessionPatternsJson = remote.SessionPatterns != null
                    ? JsonSerializer.Serialize(remote.SessionPatterns, JsonOptions)
                    : null,
                QueryConfigJson = remote.QueryConfig != null
                    ? JsonSerializer.Serialize(remote.QueryConfig, JsonOptions)
                    : null,
                IsActive = remote.IsActive,
                Priority = remote.Priority,
                Source = remote.Source ?? "community",
                CreatedAt = remote.CreatedAt,
                UpdatedAt = remote.UpdatedAt,
                LastSyncedAt = DateTime.UtcNow
            };

            _db.SceneMappings.Add(mapping);
            result.Added++;
        }
        else if (existing.Source == "local")
        {
            // Don't overwrite local mappings, just update remote ID
            existing.RemoteId = remote.Id;
            existing.LastSyncedAt = DateTime.UtcNow;
            result.Unchanged++;
        }
        else if (existing.UpdatedAt < remote.UpdatedAt)
        {
            // Update existing remote mapping
            existing.RemoteId = remote.Id;
            existing.SportType = remote.SportType;
            existing.LeagueId = remote.LeagueId;
            existing.LeagueName = remote.LeagueName;
            existing.SceneNames = remote.SceneNames ?? new List<string>();
            existing.SessionPatternsJson = remote.SessionPatterns != null
                ? JsonSerializer.Serialize(remote.SessionPatterns, JsonOptions)
                : null;
            existing.QueryConfigJson = remote.QueryConfig != null
                ? JsonSerializer.Serialize(remote.QueryConfig, JsonOptions)
                : null;
            existing.IsActive = remote.IsActive;
            existing.Priority = remote.Priority;
            existing.Source = remote.Source ?? "community";
            existing.UpdatedAt = remote.UpdatedAt;
            existing.LastSyncedAt = DateTime.UtcNow;

            result.Updated++;
        }
        else
        {
            existing.LastSyncedAt = DateTime.UtcNow;
            result.Unchanged++;
        }
    }

    /// <summary>
    /// Get all active scene mappings, ordered by priority (local overrides first)
    /// </summary>
    public async Task<List<SceneMapping>> GetActiveMappingsAsync()
    {
        return await _db.SceneMappings
            .Where(m => m.IsActive)
            .OrderByDescending(m => m.Source == "local" ? 1 : 0) // Local first
            .ThenByDescending(m => m.Priority)
            .ThenBy(m => m.SportType)
            .ToListAsync();
    }

    /// <summary>
    /// Get scene mapping for a specific sport type and optional league
    /// </summary>
    public async Task<SceneMapping?> GetMappingAsync(string sportType, string? leagueId = null)
    {
        // Try exact match first (sport + league)
        if (!string.IsNullOrEmpty(leagueId))
        {
            var exactMatch = await _db.SceneMappings
                .Where(m => m.IsActive && m.SportType == sportType && m.LeagueId == leagueId)
                .OrderByDescending(m => m.Source == "local" ? 1 : 0)
                .ThenByDescending(m => m.Priority)
                .FirstOrDefaultAsync();

            if (exactMatch != null)
                return exactMatch;
        }

        // Fall back to sport-wide mapping
        return await _db.SceneMappings
            .Where(m => m.IsActive && m.SportType == sportType && m.LeagueId == null)
            .OrderByDescending(m => m.Source == "local" ? 1 : 0)
            .ThenByDescending(m => m.Priority)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Get session patterns for a sport/league
    /// </summary>
    public async Task<SessionPatterns?> GetSessionPatternsAsync(string sportType, string? leagueId = null)
    {
        var mapping = await GetMappingAsync(sportType, leagueId);
        if (mapping?.SessionPatternsJson == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<SessionPatterns>(mapping.SessionPatternsJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get query config for a sport/league
    /// </summary>
    public async Task<QueryConfig?> GetQueryConfigAsync(string sportType, string? leagueId = null)
    {
        var mapping = await GetMappingAsync(sportType, leagueId);
        if (mapping?.QueryConfigJson == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<QueryConfig>(mapping.QueryConfigJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if a release name matches any scene name patterns for a sport/league
    /// </summary>
    public async Task<bool> MatchesSceneNameAsync(string releaseName, string sportType, string? leagueId = null)
    {
        var mapping = await GetMappingAsync(sportType, leagueId);
        if (mapping?.SceneNames == null || mapping.SceneNames.Count == 0)
            return false;

        var lowerRelease = releaseName.ToLowerInvariant();
        return mapping.SceneNames.Any(sn =>
            lowerRelease.Contains(sn.ToLowerInvariant().Replace(".", " ").Replace("_", " ")));
    }

    /// <summary>
    /// Create a local override mapping
    /// </summary>
    public async Task<SceneMapping> CreateLocalMappingAsync(
        string sportType,
        string? leagueId,
        string? leagueName,
        List<string> sceneNames,
        SessionPatterns? sessionPatterns = null,
        QueryConfig? queryConfig = null,
        int priority = 100)
    {
        var mapping = new SceneMapping
        {
            SportType = sportType,
            LeagueId = leagueId,
            LeagueName = leagueName,
            SceneNames = sceneNames,
            SessionPatternsJson = sessionPatterns != null
                ? JsonSerializer.Serialize(sessionPatterns, JsonOptions)
                : null,
            QueryConfigJson = queryConfig != null
                ? JsonSerializer.Serialize(queryConfig, JsonOptions)
                : null,
            IsActive = true,
            Priority = priority,
            Source = "local",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.SceneMappings.Add(mapping);
        await _db.SaveChangesAsync();

        _logger.LogInformation("[SceneMapping] Created local mapping for {SportType}/{LeagueId}",
            sportType, leagueId ?? "all");

        return mapping;
    }

    /// <summary>
    /// Delete a local mapping
    /// </summary>
    public async Task<bool> DeleteLocalMappingAsync(int id)
    {
        var mapping = await _db.SceneMappings.FindAsync(id);
        if (mapping == null || mapping.Source != "local")
            return false;

        _db.SceneMappings.Remove(mapping);
        await _db.SaveChangesAsync();

        _logger.LogInformation("[SceneMapping] Deleted local mapping {Id}", id);
        return true;
    }
}

/// <summary>
/// Result of a scene mapping sync operation
/// </summary>
public class SceneMappingSyncResult
{
    public bool Success { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// DTO for scene mapping from API
/// </summary>
public class SceneMappingDto
{
    public int Id { get; set; }
    public string SportType { get; set; } = string.Empty;
    public string? LeagueId { get; set; }
    public string? LeagueName { get; set; }
    public List<string>? SceneNames { get; set; }
    public JsonElement? SessionPatterns { get; set; }
    public JsonElement? QueryConfig { get; set; }
    public bool IsActive { get; set; }
    public int Priority { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// API response wrapper for scene mappings
/// </summary>
public class SceneMappingApiResponse
{
    public List<SceneMappingDto>? Mappings { get; set; }
    public int Count { get; set; }
    public string? LastUpdate { get; set; }
}
