using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using System.Text.Json;

namespace Sportarr.Endpoints;

public static class EventsEndpoints
{
    public static WebApplication MapEventsEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/events")
            .MapGetAllEventsEndpoint()
            .MapGetEventEndpoint()
            .MapCreateEventEndpoint()
            .MapUpdateEventEndpoint()
            .MapDeleteEventEndpoint()
            .MapGetFilesForEventEndpoint()
            .MapDeleteEventFileEndpoint()
            .MapDeleteAllEventFilesEndpoint()
            .MapUpdateMonitoredPartsForEventEndpoint();

        return app;
    }

    private static RouteGroupBuilder MapGetAllEventsEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/", async (SportarrDbContext db) =>
        {
            var events = await db.Events
                .Include(e => e.League)        // Universal (UFC, Premier League, NBA, etc.)
                .Include(e => e.HomeTeam)      // Universal (team sports and combat sports)
                .Include(e => e.AwayTeam)      // Universal (team sports and combat sports)
                .Include(e => e.Files)         // Event files (for multi-part episodes)
                .OrderByDescending(e => e.EventDate)
                .ToListAsync();

            // Convert to DTOs to avoid JsonPropertyName serialization issues
            var response = events.Select(EventResponse.FromEvent).ToList();
            return Results.Ok(response);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapGetEventEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/{id:int}", async (int id, SportarrDbContext db) =>
        {
            var evt = await db.Events
                .Include(e => e.League)        // Universal (UFC, Premier League, NBA, etc.)
                .Include(e => e.HomeTeam)      // Universal (team sports and combat sports)
                .Include(e => e.AwayTeam)      // Universal (team sports and combat sports)
                .Include(e => e.Files)         // Event files (for multi-part episodes)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (evt is null) return Results.NotFound();

            // Return DTO to avoid JsonPropertyName serialization issues
            return Results.Ok(EventResponse.FromEvent(evt));
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapCreateEventEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/", async (CreateEventRequest request, SportarrDbContext db) =>
        {
            var evt = new Event
            {
                ExternalId = request.ExternalId,
                Title = request.Title,
                Sport = request.Sport,           // Universal: Fighting, Soccer, Basketball, etc.
                LeagueId = request.LeagueId,     // Universal: UFC, Premier League, NBA
                HomeTeamId = request.HomeTeamId, // Team sports and combat sports
                AwayTeamId = request.AwayTeamId, // Team sports and combat sports
                Season = request.Season,
                Round = request.Round,
                EventDate = request.EventDate,
                Venue = request.Venue,
                Location = request.Location,
                Broadcast = request.Broadcast,
                Status = request.Status,
                Monitored = request.Monitored,
                QualityProfileId = request.QualityProfileId,
                Images = request.Images ?? new List<string>()
            };

            // Check if event already exists (by ExternalId OR by Title + EventDate)
            var existingEvent = await db.Events
                .Include(e => e.League)
                .Include(e => e.HomeTeam)
                .Include(e => e.AwayTeam)
                .FirstOrDefaultAsync(e =>
                    (e.ExternalId != null && e.ExternalId == evt.ExternalId) ||
                    (e.Title == evt.Title && e.EventDate.Date == evt.EventDate.Date));

            if (existingEvent != null)
            {
                // Event already exists - return it with AlreadyAdded flag
                return Results.Ok(new { Event = existingEvent, AlreadyAdded = true });
            }

            db.Events.Add(evt);
            await db.SaveChangesAsync();

            // Reload event with related entities
            var createdEvent = await db.Events
                .Include(e => e.League)
                .Include(e => e.HomeTeam)
                .Include(e => e.AwayTeam)
                .FirstOrDefaultAsync(e => e.Id == evt.Id);

            if (createdEvent is null) return Results.Problem("Failed to create event");

            return Results.Created($"/api/events/{evt.Id}", createdEvent);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapUpdateEventEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPut("/{id:int}", async (int id, JsonElement body, SportarrDbContext db, EventDvrService eventDvrService) =>
        {
            var evt = await db.Events.FindAsync(id);
            if (evt is null) return Results.NotFound();

            // Track if monitoring changed to trigger DVR scheduling
            var wasMonitored = evt.Monitored;

            // Extract fields from request body (only update fields that are present)
            if (body.TryGetProperty("title", out var titleValue))
                evt.Title = titleValue.GetString() ?? evt.Title;

            if (body.TryGetProperty("sport", out var sportValue))
                evt.Sport = sportValue.GetString() ?? evt.Sport;

            if (body.TryGetProperty("leagueId", out var leagueIdValue))
            {
                if (leagueIdValue.ValueKind == JsonValueKind.Null)
                    evt.LeagueId = null;
                else if (leagueIdValue.ValueKind == JsonValueKind.Number)
                    evt.LeagueId = leagueIdValue.GetInt32();
            }

            if (body.TryGetProperty("eventDate", out var dateValue))
                evt.EventDate = dateValue.GetDateTime();

            if (body.TryGetProperty("venue", out var venueValue))
                evt.Venue = venueValue.GetString();

            if (body.TryGetProperty("location", out var locationValue))
                evt.Location = locationValue.GetString();

            if (body.TryGetProperty("monitored", out var monitoredValue))
                evt.Monitored = monitoredValue.GetBoolean();

            if (body.TryGetProperty("monitoredParts", out var monitoredPartsValue))
            {
                evt.MonitoredParts = monitoredPartsValue.ValueKind == JsonValueKind.Null
                    ? null
                    : monitoredPartsValue.GetString();
            }

            if (body.TryGetProperty("qualityProfileId", out var qualityProfileIdValue))
            {
                if (qualityProfileIdValue.ValueKind == JsonValueKind.Null)
                    evt.QualityProfileId = null;
                else if (qualityProfileIdValue.ValueKind == JsonValueKind.Number)
                    evt.QualityProfileId = qualityProfileIdValue.GetInt32();
            }

            evt.LastUpdate = DateTime.UtcNow;

            await db.SaveChangesAsync();

            // Handle DVR scheduling when monitoring changes
            if (wasMonitored != evt.Monitored)
            {
                await eventDvrService.HandleEventMonitoringChangeAsync(id, evt.Monitored);
            }

            // Reload with related entities
            evt = await db.Events
                .Include(e => e.League)
                .Include(e => e.HomeTeam)
                .Include(e => e.AwayTeam)
                .Include(e => e.Files)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (evt is null) return Results.NotFound();

            // Return DTO to avoid JsonPropertyName serialization issues
            return Results.Ok(EventResponse.FromEvent(evt));
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapDeleteEventEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/{id:int}", async (int id, SportarrDbContext db) =>
        {
            var evt = await db.Events.FindAsync(id);
            if (evt is null) return Results.NotFound();

            db.Events.Remove(evt);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapGetFilesForEventEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/{id:int}/files", async (int id, SportarrDbContext db) =>
        {
            var evt = await db.Events
                .Include(e => e.Files)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (evt is null) return Results.NotFound();

            // Only return files that exist on disk
            return Results.Ok(evt.Files.Where(f => f.Exists).Select(f => new
            {
                f.Id,
                f.EventId,
                f.FilePath,
                f.Size,
                f.Quality,
                f.QualityScore,
                f.CustomFormatScore,
                f.PartName,
                f.PartNumber,
                f.Added,
                f.LastVerified,
                f.Exists,
                FileName = Path.GetFileName(f.FilePath)
            }));
        });

        return routeGroup;
    }

    /// <summary>
    /// API: Delete a specific event file (removes from disk and database)
    /// blocklistAction: 'none' | 'blocklistAndSearch' | 'blocklistOnly'
    /// </summary>
    /// <param name="routeGroup"></param>
    private static RouteGroupBuilder MapDeleteEventFileEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/{eventId:int}/files/{fileId:int}", async ( int eventId,
            int fileId,
            string? blocklistAction,
            SportarrDbContext db,
            ILogger<Program> logger,
            IConfigService configService,
            IAutomaticSearchService searchService) =>
            {
                var evt = await db.Events
                    .Include(e => e.Files)
                    .Include(e => e.League)
                    .FirstOrDefaultAsync(e => e.Id == eventId);

                if (evt is null)
                    return Results.NotFound(new { error = "Event not found" });

                var file = evt.Files.FirstOrDefault(f => f.Id == fileId);
                if (file is null)
                    return Results.NotFound(new { error = "File not found" });

                logger.LogInformation("[FILES] Deleting file {FileId} for event {EventId}: {FilePath} (blocklistAction={BlocklistAction})",
                    fileId, eventId, file.FilePath, blocklistAction ?? "none");

                // Delete from disk if it exists
                bool deletedFromDisk = false;
                if (File.Exists(file.FilePath))
                {
                    try
                    {
                        // Check if recycle bin is configured
                        var config = await configService.GetConfigAsync();
                        var recycleBinPath = config.RecycleBin;

                        if (!string.IsNullOrEmpty(recycleBinPath) && Directory.Exists(recycleBinPath))
                        {
                            // Move to recycle bin instead of permanent deletion
                            var fileName = Path.GetFileName(file.FilePath);
                            var recyclePath = Path.Combine(recycleBinPath, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{fileName}");
                            File.Move(file.FilePath, recyclePath);
                            logger.LogInformation("[FILES] Moved file to recycle bin: {RecyclePath}", recyclePath);
                        }
                        else
                        {
                            // Permanent deletion
                            File.Delete(file.FilePath);
                            logger.LogInformation("[FILES] Permanently deleted file from disk");
                        }
                        deletedFromDisk = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[FILES] Failed to delete file from disk: {FilePath}", file.FilePath);
                        return Results.Problem(
                            detail: $"Failed to delete file from disk: {ex.Message}",
                            statusCode: 500);
                    }
                }
                else
                {
                    logger.LogWarning("[FILES] File not found on disk (already deleted?): {FilePath}", file.FilePath);
                }

                // Remove from database
                db.Remove(file);

                // Update event's HasFile status
                var remainingFiles = evt.Files.Where(f => f.Id != fileId && f.Exists).ToList();
                if (!remainingFiles.Any())
                {
                    evt.HasFile = false;
                    evt.FilePath = null;
                    evt.FileSize = null;
                    evt.Quality = null;
                    logger.LogInformation("[FILES] Event {EventId} no longer has any files", eventId);
                }
                else
                {
                    // Update to use the first remaining file's info
                    var primaryFile = remainingFiles.First();
                    evt.FilePath = primaryFile.FilePath;
                    evt.FileSize = primaryFile.Size;
                    evt.Quality = primaryFile.Quality;
                }

                await db.SaveChangesAsync();

                // Handle blocklist action if specified
                if (blocklistAction == "blocklistAndSearch" || blocklistAction == "blocklistOnly")
                {
                    // Add to blocklist using originalTitle if available, otherwise use filename
                    var releaseTitle = file.OriginalTitle ?? Path.GetFileNameWithoutExtension(file.FilePath);
                    if (!string.IsNullOrEmpty(releaseTitle))
                    {
                        var blocklistEntry = new BlocklistItem
                        {
                            EventId = eventId,
                            Title = releaseTitle,
                            TorrentInfoHash = $"manual-block-{DateTime.UtcNow.Ticks}", // Synthetic hash for non-torrent blocks
                            Reason = BlocklistReason.ManualBlock,
                            Message = "Deleted from file management",
                            BlockedAt = DateTime.UtcNow
                        };
                        db.Blocklist.Add(blocklistEntry);
                        await db.SaveChangesAsync();
                        logger.LogInformation("[FILES] Added release to blocklist: {Title}", releaseTitle);
                    }

                    // Trigger search for replacement if requested
                    if (blocklistAction == "blocklistAndSearch" && evt.Monitored)
                    {
                        // Use event's profile first, then league's, then let AutomaticSearchService handle fallback
                        var qualityProfileId = evt.QualityProfileId ?? evt.League?.QualityProfileId;
                        var partName = file.PartName;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                logger.LogInformation("[FILES] Searching for replacement for event {EventId}, part: {Part}", eventId, partName ?? "all");
                                await searchService.SearchAndDownloadEventAsync(eventId, qualityProfileId, partName, isManualSearch: true);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "[FILES] Failed to search for replacement for event {EventId}", eventId);
                            }
                        });
                    }
                }

                return Results.Ok(new
                {
                    success = true,
                    message = deletedFromDisk ? "File deleted from disk and database" : "File removed from database (was not found on disk)",
                    eventHasFiles = remainingFiles.Any()
                });
            }
        );

        return routeGroup;
    }

    /// <summary>
    /// API: Delete all files for an event
    /// blocklistAction: 'none' | 'blocklistAndSearch' | 'blocklistOnly'
    /// </summary>
    /// <param name="routeGroup"></param>
    private static RouteGroupBuilder MapDeleteAllEventFilesEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/{id:int}/files", async (int id,
            string? blocklistAction,
            SportarrDbContext db,
            ILogger<Program> logger,
            IConfigService configService,
            IAutomaticSearchService searchService) =>
            {
                var evt = await db.Events
                    .Include(e => e.Files)
                    .Include(e => e.League)
                    .FirstOrDefaultAsync(e => e.Id == id);

                if (evt is null)
                    return Results.NotFound(new { error = "Event not found" });

                if (!evt.Files.Any())
                    return Results.Ok(new { success = true, message = "No files to delete", deletedCount = 0 });

                logger.LogInformation("[FILES] Deleting all {Count} files for event {EventId} (blocklistAction={BlocklistAction})",
                    evt.Files.Count, id, blocklistAction ?? "none");

                // Collect original titles for blocklisting before deletion
                var releasesToBlocklist = evt.Files
                    .Select(f => f.OriginalTitle ?? Path.GetFileNameWithoutExtension(f.FilePath))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct()
                    .ToList();

                var config = await configService.GetConfigAsync();
                var recycleBinPath = config.RecycleBin;
                var useRecycleBin = !string.IsNullOrEmpty(recycleBinPath) && Directory.Exists(recycleBinPath);

                int deletedFromDisk = 0;
                int failedToDelete = 0;

                foreach (var file in evt.Files.ToList())
                {
                    if (File.Exists(file.FilePath))
                    {
                        try
                        {
                            if (useRecycleBin)
                            {
                                var fileName = Path.GetFileName(file.FilePath);
                                var recyclePath = Path.Combine(recycleBinPath!, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{fileName}");
                                File.Move(file.FilePath, recyclePath);
                            }
                            else
                            {
                                File.Delete(file.FilePath);
                            }
                            deletedFromDisk++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[FILES] Failed to delete file: {FilePath}", file.FilePath);
                            failedToDelete++;
                        }
                    }
                }

                // Remove all files from database
                db.RemoveRange(evt.Files);

                // Update event status
                evt.HasFile = false;
                evt.FilePath = null;
                evt.FileSize = null;
                evt.Quality = null;

                await db.SaveChangesAsync();

                // Handle blocklist action if specified
                if (blocklistAction == "blocklistAndSearch" || blocklistAction == "blocklistOnly")
                {
                    // Add all releases to blocklist
                    foreach (var releaseTitle in releasesToBlocklist)
                    {
                        var blocklistEntry = new BlocklistItem
                        {
                            EventId = id,
                            Title = releaseTitle!,
                            TorrentInfoHash = $"manual-block-{DateTime.UtcNow.Ticks}-{releaseTitle!.GetHashCode()}", // Synthetic hash
                            Reason = BlocklistReason.ManualBlock,
                            Message = "Deleted from file management (delete all)",
                            BlockedAt = DateTime.UtcNow
                        };
                        db.Blocklist.Add(blocklistEntry);
                    }
                    await db.SaveChangesAsync();
                    logger.LogInformation("[FILES] Added {Count} releases to blocklist", releasesToBlocklist.Count);

                    // Trigger search for replacements if requested
                    if (blocklistAction == "blocklistAndSearch" && evt.Monitored)
                    {
                        // Use event's profile first, then league's, then let AutomaticSearchService handle fallback
                        var qualityProfileId = evt.QualityProfileId ?? evt.League?.QualityProfileId;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                logger.LogInformation("[FILES] Searching for replacement for event {EventId}", id);
                                await searchService.SearchAndDownloadEventAsync(id, qualityProfileId, null, isManualSearch: true);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "[FILES] Failed to search for replacement for event {EventId}", id);
                            }
                        });
                    }
                }

                var message = failedToDelete > 0
                    ? $"Deleted {deletedFromDisk} files, {failedToDelete} failed to delete from disk"
                    : $"Deleted {deletedFromDisk} files";

                logger.LogInformation("[FILES] {Message} for event {EventId}", message, id);

                return Results.Ok(new
                {
                    success = failedToDelete == 0,
                    message,
                    deletedCount = deletedFromDisk,
                    failedCount = failedToDelete
                });
            }
        );

        return routeGroup;
    }

    /// <summary>
    /// API: Update event monitored parts (for fighting sports multi-part episodes)
    /// </summary>
    /// <param name="routeGroup"></param>
    private static RouteGroupBuilder MapUpdateMonitoredPartsForEventEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPut("/{id:int}/parts", async (int id, JsonElement body, SportarrDbContext db, ILogger<Program> logger) =>
        {
            var evt = await db.Events.FindAsync(id);
            if (evt is null) return Results.NotFound();

            if (body.TryGetProperty("monitoredParts", out var partsValue))
            {
                evt.MonitoredParts = partsValue.ValueKind == JsonValueKind.Null
                    ? null
                    : partsValue.GetString();

                logger.LogInformation("[EVENT] Updated monitored parts for event {EventId} ({EventTitle}) to: {Parts}",
                    id, evt.Title, evt.MonitoredParts ?? "null (use league default)");
            }

            evt.LastUpdate = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(evt);
        });

        return routeGroup;
    }
}
