using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Endpoints;

public static class LibraryEndpoints
{
    public static WebApplication MapLibraryEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/library")
            .MapScanEndpoint()
            .MapImportEndpoint()
            .MapPreviewEndpoint()
            .MapSearchEndpoint()
            .MapLeaguesEndpoints()
            .MapPartsEndpoint();

        return app;
    }

    private static RouteGroupBuilder MapScanEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/scan", async (LibraryImportService service, string folderPath, bool includeSubfolders = true) =>
        {
            try
            {
                var result = await service.ScanFolderAsync(folderPath, includeSubfolders);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to scan folder: {ex.Message}");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapImportEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/import", async (LibraryImportService service, List<FileImportRequest> requests) =>
        {
            try
            {
                var result = await service.ImportFilesAsync(requests);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to import files: {ex.Message}");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapPreviewEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/preview", async (LibraryImportService service, int eventId, string fileName) =>
        {
            try
            {
                var preview = await service.BuildDestinationPreviewForEventAsync(eventId, fileName);

                if (preview == null)
                    return Results.NotFound(new { error = "Event not found" });

                return Results.Ok(new { destinationPreview = preview });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to build preview: {ex.Message}");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapSearchEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/search", async (SportarrApiClient sportarrApi,
            SportarrDbContext db,
            string query,
            string? sport = null,
            string? organization = null) =>
            {
                try
                {
                    var results = new List<object>();

                    // Search Sportarr event database (data sourced from sports data API)
                    var apiEvents = await sportarrApi.SearchEventAsync(query);
                    if (apiEvents != null)
                    {
                        foreach (var evt in apiEvents.Take(20)) // Limit to 20 results
                        {
                            // Check if event already exists in local database
                            var existingEvent = await db.Events
                                .FirstOrDefaultAsync(e => e.ExternalId == evt.ExternalId);

                            results.Add(new
                            {
                                id = existingEvent?.Id,
                                externalId = evt.ExternalId,
                                title = evt.Title,
                                sport = evt.Sport,
                                eventDate = evt.EventDate,
                                venue = evt.Venue,
                                leagueName = evt.League?.Name,
                                homeTeam = evt.HomeTeam?.Name,
                                awayTeam = evt.AwayTeam?.Name,
                                existsInDatabase = existingEvent != null,
                                hasFile = existingEvent?.HasFile ?? false
                            });
                        }
                    }

                    // Also search local database for events that might match
                    var localQuery = db.Events
                        .Include(e => e.League)
                        .Include(e => e.HomeTeam)
                        .Include(e => e.AwayTeam)
                        .Where(e => !e.HasFile) // Only events without files
                        .AsQueryable();

                    if (!string.IsNullOrEmpty(sport))
                    {
                        localQuery = localQuery.Where(e => e.Sport == sport);
                    }

                    var localEvents = await localQuery
                        .Where(e => EF.Functions.Like(e.Title, $"%{query}%"))
                        .Take(20)
                        .ToListAsync();

                    foreach (var evt in localEvents)
                    {
                        // Don't duplicate if already in results from API
                        if (!results.Any(r => ((dynamic)r).externalId == evt.ExternalId))
                        {
                            results.Add(new
                            {
                                id = evt.Id,
                                externalId = evt.ExternalId,
                                title = evt.Title,
                                sport = evt.Sport,
                                eventDate = evt.EventDate,
                                venue = evt.Venue,
                                leagueName = evt.League?.Name,
                                homeTeam = evt.HomeTeam?.Name,
                                awayTeam = evt.AwayTeam?.Name,
                                existsInDatabase = true,
                                hasFile = evt.HasFile
                            });
                        }
                    }

                    return Results.Ok(new { results });
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Failed to search events: {ex.Message}");
                }
            }
        );

        return routeGroup;
    }

    private static RouteGroupBuilder MapLeaguesEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/leagues/{leagueId:int}/seasons", async (int leagueId, SportarrDbContext db) =>
        {
            try
            {
                var seasons = await db.Events
                    .Where(e => e.LeagueId == leagueId && !string.IsNullOrEmpty(e.Season))
                    .Select(e => e.Season)
                    .Distinct()
                    .OrderByDescending(s => s)
                    .ToListAsync();

                return Results.Ok(new { seasons });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to get seasons: {ex.Message}");
            }
        });

        routeGroup.MapGet("/leagues/{leagueId:int}/events", async (int leagueId,
            SportarrDbContext db,
            IConfigService configService,
            string? season = null,
            string? search = null,
            int limit = 100) =>
            {
                try
                {
                    var query = db.Events
                        .Include(e => e.League)
                        .Include(e => e.HomeTeam)
                        .Include(e => e.AwayTeam)
                        .Include(e => e.Files)
                        .Where(e => e.LeagueId == leagueId);

                    if (!string.IsNullOrEmpty(season))
                    {
                        query = query.Where(e => e.Season == season);
                    }

                    // Server-side search - search across title, team names, venue, season
                    if (!string.IsNullOrEmpty(search))
                    {
                        var searchLower = search.ToLower();
                        query = query.Where(e =>
                            e.Title.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                            (e.HomeTeamName != null && e.HomeTeamName.Contains(searchLower, StringComparison.OrdinalIgnoreCase)) ||
                            (e.AwayTeamName != null && e.AwayTeamName.Contains(searchLower, StringComparison.OrdinalIgnoreCase)) ||
                            (e.HomeTeam != null && e.HomeTeam.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase)) ||
                            (e.AwayTeam != null && e.AwayTeam.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase)) ||
                            (e.Venue != null && e.Venue.Contains(searchLower, StringComparison.OrdinalIgnoreCase)) ||
                            (e.Season != null && e.Season.Contains(searchLower, StringComparison.OrdinalIgnoreCase)) ||
                            (e.ExternalId != null && e.ExternalId.Contains(searchLower, StringComparison.OrdinalIgnoreCase))
                        );
                    }

                    // Clamp limit to reasonable bounds
                    limit = Math.Clamp(limit, 10, 500);

                    var events = await query
                        .OrderByDescending(e => e.EventDate)
                        .Take(limit)
                        .ToListAsync();

                    var config = await configService.GetConfigAsync();

                    var results = events.Select(e => new
                    {
                        id = e.Id,
                        externalId = e.ExternalId,
                        title = e.Title,
                        sport = e.Sport,
                        eventDate = e.EventDate,
                        season = e.Season,
                        seasonNumber = e.SeasonNumber,
                        episodeNumber = e.EpisodeNumber,
                        venue = e.Venue,
                        leagueName = e.League?.Name,
                        homeTeam = e.HomeTeam?.Name ?? e.HomeTeamName,
                        awayTeam = e.AwayTeam?.Name ?? e.AwayTeamName,
                        hasFile = e.HasFile,
                        // Include part info for multi-part sports
                        usesMultiPart = config.EnableMultiPartEpisodes &&
                            (EventPartDetector.IsFightingSport(e.Sport) ||
                             EventPartDetector.IsMotorsport(e.Sport)),
                        files = e.Files.Select(f => new
                        {
                            id = f.Id,
                            partName = f.PartName,
                            partNumber = f.PartNumber,
                            quality = f.Quality
                        }).ToList()
                    }).ToList();

                    return Results.Ok(new { events = results });
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Failed to get events: {ex.Message}");
                }
            }
        );

        return routeGroup;
    }

    private static RouteGroupBuilder MapPartsEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/parts/{sport}", (string sport) =>
        {
            var segments = Sportarr.Api.Services.EventPartDetector.GetSegmentDefinitions(sport);
            return Results.Ok(new { parts = segments });
        });

        return routeGroup;
    }
}
