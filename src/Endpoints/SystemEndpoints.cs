using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.Json;

namespace Sportarr.Endpoints;

public static class SystemEndpoints
{
    private const string C_SPORTARR_LATEST = "https://github.com/Sportarr/Sportarr/releases/latest";
    private const string C_SPORTARR_RELEASES = "https://api.github.com/repos/Sportarr/Sportarr/releases";

    public static WebApplication MapSystemEndpoints(this WebApplication app, string dataPath)
    {
        app.MapGroup("/api/system")
            .MapStatusEndpoint(app, dataPath)
            .MapTimezonesEndpoint()
            .MapHealthCheckEndpoint()
            .MapBackupEndpoints()
            .MapAgentsEndpoints(dataPath)
            .MapUpdatesEndpoint()
            .MapEventEndpoints()
            .MapDiskScanEndpoint();

        return app;
    }

    private static RouteGroupBuilder MapStatusEndpoint(this RouteGroupBuilder routeGroup, WebApplication app, string dataPath)
    {
        routeGroup.MapGet("/status", async (IConfigService configService) =>
        {
            var config = await configService.GetConfigAsync();
            var status = new SystemStatus
            {
                AppName = "Sportarr",
                Version = Sportarr.Api.Version.GetFullVersion(),  // Use full 4-part version (e.g., 4.0.81.140)
                IsDebug = app.Environment.IsDevelopment(),
                IsProduction = app.Environment.IsProduction(),
                IsDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
                DatabaseType = "SQLite",
                Authentication = "apikey",
                AppData = dataPath,
                StartTime = DateTime.UtcNow,
                TimeZone = string.IsNullOrEmpty(config.TimeZone) ? TimeZoneInfo.Local.Id : config.TimeZone
            };
            return Results.Ok(status);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapTimezonesEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/timezones", () =>
        {
            var timezones = TimeZoneInfo.GetSystemTimeZones()
                .Select(tz => new
                {
                    id = tz.Id,
                    displayName = tz.DisplayName,
                    standardName = tz.StandardName,
                    baseUtcOffset = tz.BaseUtcOffset.TotalHours
                })
                .OrderBy(tz => tz.baseUtcOffset)
                .ThenBy(tz => tz.displayName)
                .ToList();

            return Results.Ok(new
            {
                currentTimeZone = TimeZoneInfo.Local.Id,
                timezones
            });
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapHealthCheckEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/health", async (HealthCheckService healthCheckService) =>
        {
            var healthResults = await healthCheckService.PerformAllChecksAsync();
            return Results.Ok(healthResults);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/backup", async (BackupService backupService) =>
        {
            var backups = await backupService.GetBackupsAsync();
            return Results.Ok(backups);
        });

        routeGroup.MapPost("/backup", async (BackupService backupService, string? note) =>
        {
            try
            {
                var backup = await backupService.CreateBackupAsync(note);
                return Results.Ok(backup);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        routeGroup.MapDelete("/backup/{backupName}", async (string backupName, BackupService backupService) =>
        {
            try
            {
                await backupService.DeleteBackupAsync(backupName);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        routeGroup.MapPost("/backup/restore/{backupName}", async (string backupName, BackupService backupService) =>
        {
            try
            {
                await backupService.RestoreBackupAsync(backupName);
                return Results.Ok(new { message = "Backup restored successfully. Please restart Sportarr for changes to take effect." });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        routeGroup.MapGet("/backup/download/{backupName}", async (string backupName, 
            BackupService backupService, 
            IFileSystem fileSystem) =>
        {
            try
            {
                var backups = await backupService.GetBackupsAsync();
                var backup = backups.FirstOrDefault(b => b.Name == backupName);

                if (backup == null || !fileSystem.File.Exists(backup.Path))
                    return Results.NotFound(new { message = "Backup file not found" });

                return Results.File(backup.Path, "application/zip", backupName);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        routeGroup.MapPost("/backup/cleanup", async (BackupService backupService) =>
        {
            try
            {
                await backupService.CleanupOldBackupsAsync();
                return Results.Ok(new { message = "Old backups cleaned up successfully" });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapAgentsEndpoints(this RouteGroupBuilder routeGroup, string dataPath)
    {
        routeGroup.MapGet("/agents", 
            () =>
            {
                var agents = new List<object>
                {
                    new
                    {
                        name = "Plex",
                        type = "plex",
                        available = true,
                        downloadUrl = "/api/system/agents/plex/download"
                    },
                    new
                    {
                        name = "Jellyfin",
                        type = "jellyfin",
                        available = true,
                        downloadUrl = "/api/system/agents/jellyfin/download",
                        repositoryUrl = "https://raw.githubusercontent.com/sportarr/Sportarr/main/agents/jellyfin/manifest.json"
                    },
                    new
                    {
                        name = "Emby",
                        type = "emby",
                        available = true,
                        downloadUrl = "/api/system/agents/emby/download"
                    }
                };

                return Results.Ok(new { agents });
            }
        );

        routeGroup.MapGet("/agents/plex/download", async (HttpContext context, ILogger<Program> logger, IFileSystem fileSystem) =>
        {
            // Try config directory first, then fall back to app directory
            var plexAgentPath = fileSystem.Path.Combine(dataPath, "agents", "plex", "Sportarr.bundle");
            logger.LogInformation("Checking for Plex agent at: {Path}", plexAgentPath);

            if (!fileSystem.Directory.Exists(plexAgentPath))
            {
                plexAgentPath = fileSystem.Path.Combine(AppContext.BaseDirectory, "agents", "plex", "Sportarr.bundle");
                logger.LogInformation("Not found, checking fallback at: {Path}", plexAgentPath);
            }

            if (!fileSystem.Directory.Exists(plexAgentPath))
            {
                logger.LogWarning("Plex agent not found at either location");
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Plex agent not found. The agents folder may not be included in your build.\"}");
                return;
            }

            try
            {
                logger.LogInformation("Creating zip from: {Path}", plexAgentPath);

                // Create a zip file in memory
                using var memoryStream = new MemoryStream();
                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                {
                    await AddDirectoryToZip(fileSystem, archive, plexAgentPath, "Sportarr.bundle");
                }

                memoryStream.Position = 0;
                var bytes = memoryStream.ToArray();

                logger.LogInformation("Zip created successfully, size: {Size} bytes", bytes.Length);

                context.Response.ContentType = "application/zip";
                context.Response.Headers.Append("Content-Disposition", "attachment; filename=\"Sportarr.bundle.zip\"");
                context.Response.Headers.Append("Content-Length", bytes.Length.ToString());
                await context.Response.Body.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create Plex agent zip");
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"error\":\"Failed to create zip: {ex.Message}\"}}");
            }
        });

        routeGroup.MapGet("/agents/jellyfin/download", async (HttpContext context, ILogger<Program> logger) =>
        {
            // Redirect to compiled plugin from latest GitHub release
            var downloadUrl = await GetPluginDownloadUrl("jellyfin", logger);

            if (downloadUrl != null)
            {
                context.Response.Redirect(downloadUrl, permanent: false);
                return;
            }

            // Fallback: redirect to GitHub releases page
            logger.LogWarning("Could not find Jellyfin plugin asset in GitHub releases, redirecting to releases page");

            //TODO: Same here, just probably come from config
            context.Response.Redirect(C_SPORTARR_LATEST, permanent: false);
        });

        routeGroup.MapGet("/agents/emby/download", async (HttpContext context, ILogger<Program> logger) =>
        {
            // Redirect to compiled plugin from latest GitHub release
            var downloadUrl = await GetPluginDownloadUrl("emby", logger);

            if (downloadUrl != null)
            {
                context.Response.Redirect(downloadUrl, permanent: false);
                return;
            }

            // Fallback: redirect to GitHub releases page
            logger.LogWarning("Could not find Emby plugin asset in GitHub releases, redirecting to releases page");

            //TODO: Same here, just probably come from config
            context.Response.Redirect(C_SPORTARR_LATEST, permanent: false);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapUpdatesEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/updates", async (ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[UPDATES] Checking for updates from GitHub");

                // Get current version using the centralized version helper
                var currentVersion = Sportarr.Api.Version.GetFullVersion();

                logger.LogInformation("[UPDATES] Current version: {Version}", currentVersion);

                // Fetch releases from GitHub API
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", $"Sportarr/{currentVersion}");
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                HttpResponseMessage response;
                try
                {
                    //TODO: Should probably come from config
                    response = await httpClient.GetAsync(C_SPORTARR_RELEASES);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogError(ex, "[UPDATES] HTTP error connecting to GitHub API");
                    return Results.Problem($"Failed to connect to GitHub: {ex.Message}");
                }
                catch (TaskCanceledException ex)
                {
                    logger.LogError(ex, "[UPDATES] Request to GitHub API timed out");
                    return Results.Problem("GitHub API request timed out");
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("[UPDATES] Failed to fetch releases from GitHub: {StatusCode}", response.StatusCode);
                    return Results.Problem("Failed to fetch updates from GitHub");
                }

                var json = await response.Content.ReadAsStringAsync();

                // Handle empty or invalid JSON response
                if (string.IsNullOrWhiteSpace(json))
                {
                    logger.LogWarning("[UPDATES] GitHub returned empty response");

                    return Results.Ok(new
                    {
                        updateAvailable = false,
                        currentVersion,
                        latestVersion = currentVersion,
                        releases = new List<object>()
                    });
                }

                JsonElement releases;

                try
                {
                    releases = JsonSerializer.Deserialize<JsonElement>(json);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "[UPDATES] Failed to parse GitHub response");
                    return Results.Problem("Failed to parse GitHub response");
                }

                // Check if response is an array
                if (releases.ValueKind != JsonValueKind.Array)
                {
                    logger.LogWarning("[UPDATES] GitHub response is not an array: {Kind}", releases.ValueKind);

                    // Could be an error object from GitHub (e.g., rate limit)
                    if (releases.TryGetProperty("message", out var messageElement))
                    {
                        var errorMessage = messageElement.GetString();
                        logger.LogWarning("[UPDATES] GitHub error: {Message}", errorMessage);
                        return Results.Problem($"GitHub API error: {errorMessage}");
                    }

                    return Results.Ok(new
                    {
                        updateAvailable = false,
                        currentVersion,
                        latestVersion = currentVersion,
                        releases = new List<object>()
                    });
                }

                var releaseList = new List<object>();
                string? latestVersion = null;

                foreach (var release in releases.EnumerateArray())
                {
                    var tagName = release.GetProperty("tag_name").GetString() ?? string.Empty;
                    var version = tagName.TrimStart('v'); // Remove 'v' prefix if present
                    var publishedAt = release.GetProperty("published_at").GetString() ?? DateTime.UtcNow.ToString();
                    var body = release.GetProperty("body").GetString() ?? string.Empty;
                    var htmlUrl = release.GetProperty("html_url").GetString() ?? string.Empty;
                    var isDraft = release.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
                    var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) && prereleaseProp.GetBoolean();

                    // Skip drafts and prereleases
                    if (isDraft || isPrerelease)
                    {
                        continue;
                    }

                    // Track latest version
                    latestVersion ??= version;

                    // Parse changelog from release body
                    var changes = new List<string>();

                    if (!string.IsNullOrEmpty(body))
                    {
                        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                        foreach (var line in lines)
                        {
                            var trimmed = line.Trim();
                            // Skip headers and empty lines
                            if (trimmed.StartsWith('#') || string.IsNullOrWhiteSpace(trimmed))
                            {
                                continue;
                            }
                            // Add bullet points
                            if (trimmed.StartsWith('-') || trimmed.StartsWith('*'))
                            {
                                changes.Add(trimmed.TrimStart('-', '*').Trim());
                            }
                            else if (changes.Count < 10) // Limit to 10 changes
                            {
                                changes.Add(trimmed);
                            }
                        }
                    }

                    // Check if this release is installed (compare 3-part base version)
                    var currentParts = currentVersion.Split('.');
                    var currentBase = currentParts.Length >= 3 ? $"{currentParts[0]}.{currentParts[1]}.{currentParts[2]}" : currentVersion;
                    var isInstalled = version == currentBase || version == currentVersion;

                    releaseList.Add(new
                    {
                        version,
                        releaseDate = publishedAt,
                        branch = "main",
                        changes = changes.Take(10).ToList(), // Limit to 10 changes per release
                        downloadUrl = htmlUrl,
                        isInstalled,
                        isLatest = version == latestVersion
                    });

                    // Only show last 10 releases
                    if (releaseList.Count >= 10)
                    {
                        break;
                    }
                }

                // Compare versions properly - currentVersion is 4-part (4.0.81.140), latestVersion is 3-part (4.0.82)
                var updateAvailable = false;

                if (latestVersion != null)
                {
                    // Extract first 3 parts of current version for comparison (4.0.81.140 -> 4.0.81)
                    var currentParts = currentVersion.Split('.');
                    var currentBase = currentParts.Length >= 3 ? $"{currentParts[0]}.{currentParts[1]}.{currentParts[2]}" : currentVersion;

                    // latestVersion is already 3-part from GitHub tags (v4.0.82 -> 4.0.82)
                    updateAvailable = latestVersion != currentBase && latestVersion != currentVersion;
                }

                logger.LogInformation("[UPDATES] Current: {Current}, Latest: {Latest}, Available: {Available}",
                    currentVersion, latestVersion ?? "unknown", updateAvailable);

                return Results.Ok(new
                {
                    updateAvailable,
                    currentVersion,
                    latestVersion = latestVersion ?? currentVersion,
                    releases = releaseList
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[UPDATES] Error checking for updates");
                return Results.Problem("Error checking for updates: " + ex.Message);
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/event", async (SportarrDbContext db, int page = 1, int pageSize = 50, string? type = null, string? category = null) =>
        {
            // Validate pagination parameters
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 1;
            if (pageSize > 500) pageSize = 500; // Prevent excessive data retrieval

            var query = db.SystemEvents.AsQueryable();

            if (!string.IsNullOrEmpty(type) && Enum.TryParse<EventType>(type, true, out var eventType))
            {
                query = query.Where(e => e.Type == eventType);
            }

            if (!string.IsNullOrEmpty(category) && Enum.TryParse<EventCategory>(category, true, out var eventCategory))
            {
                query = query.Where(e => e.Category == eventCategory);
            }

            var totalCount = await query.CountAsync();
            var events = await query
                .OrderByDescending(e => e.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new
            {
                events,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
                totalRecords = totalCount
            });
        });

        routeGroup.MapDelete("/event/{id:int}", async (int id, SportarrDbContext db) =>
        {
            var systemEvent = await db.SystemEvents.FindAsync(id);

            if (systemEvent is null) 
                return Results.NotFound();

            db.SystemEvents.Remove(systemEvent);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        routeGroup.MapPost("/event/cleanup", async (SportarrDbContext db, int days = 30) =>
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-days);
            var oldEvents = db.SystemEvents.Where(e => e.Timestamp < cutoffDate);
            db.SystemEvents.RemoveRange(oldEvents);
            var deleted = await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Deleted {deleted} old system events", deletedCount = deleted });
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapDiskScanEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/disk-scan", (DiskScanService diskScanService) =>
        {
            diskScanService.TriggerScanNow();
            return Results.Ok(new { message = "Disk scan triggered successfully" });
        });

        return routeGroup;
    }

    private static async Task AddDirectoryToZip(IFileSystem fileSystem, ZipArchive archive, string sourceDir, string entryPrefix)
    {
        foreach (var file in fileSystem.Directory.GetFiles(sourceDir))
        {
            var entryName = fileSystem.Path.Combine(entryPrefix, fileSystem.Path.GetFileName(file)).Replace('\\', '/');
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            using var fileStream = fileSystem.File.OpenRead(file);
            await fileStream.CopyToAsync(entryStream);
        }

        foreach (var dir in fileSystem.Directory.GetDirectories(sourceDir))
        {
            var dirName = fileSystem.Path.GetFileName(dir);
            // Skip obj and bin directories
            if (dirName == "obj" || dirName == "bin")
                continue;

            await AddDirectoryToZip(fileSystem, archive, dir, Path.Combine(entryPrefix, dirName));
        }
    }

    private static async Task<string?> GetPluginDownloadUrl(string pluginType, ILogger logger)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"Sportarr/{Api.Version.GetFullVersion()}");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            //TODO: this should probably come from config instead of hardcoded.
            var response = await httpClient.GetAsync(C_SPORTARR_LATEST);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch latest release from GitHub: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<JsonElement>(json);

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            // Look for the plugin asset (e.g., "sportarr-jellyfin-plugin_*.zip" or "sportarr-emby-plugin_*.zip")
            var assetPrefix = $"sportarr-{pluginType}-plugin_";

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;

                if (name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var browserDownloadUrl = asset.GetProperty("browser_download_url").GetString();
                    logger.LogInformation("Found {PluginType} plugin asset: {Name} -> {Url}", pluginType, name, browserDownloadUrl);
                    return browserDownloadUrl;
                }
            }

            logger.LogWarning("No {PluginType} plugin asset found in latest release", pluginType);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get {PluginType} plugin download URL from GitHub", pluginType);
            return null;
        }
    }
}
