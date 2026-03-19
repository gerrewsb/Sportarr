using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Services;
using System.Text.Json;

namespace Sportarr.Endpoints;

public static class LeaguesEndpoints
{
    public static WebApplication MapLeaguesEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/leagues")
            .MapToggleSeasonMonitoringEndpoint();

        return app;
    }

    private static RouteGroupBuilder MapToggleSeasonMonitoringEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPut("/{leagueId:int}/seasons/{season}/toggle", async (int leagueId,
            string season,
            JsonElement body,
            SportarrDbContext db,
            ILogger<Program> logger) =>
            {
                var league = await db.Leagues.FindAsync(leagueId);
                if (league is null) return Results.NotFound("League not found");

                if (!body.TryGetProperty("monitored", out var monitoredValue))
                    return Results.BadRequest("'monitored' field is required");

                bool monitored = monitoredValue.GetBoolean();

                // Get all events for this league and season
                var events = await db.Events
                    .Where(e => e.LeagueId == leagueId && e.Season == season)
                    .ToListAsync();

                if (events.Count == 0)
                    return Results.NotFound($"No events found for season {season}");

                logger.LogInformation("[SEASON TOGGLE] {Action} season {Season} for league {LeagueName} ({EventCount} events)",
                    monitored ? "Monitoring" : "Unmonitoring", season, league.Name, events.Count);

                foreach (var evt in events)
                {
                    // Determine if this specific event should be monitored
                    // Start with the requested state
                    bool shouldMonitor = monitored;

                    // If enabling monitoring for a motorsport event, check if it matches the monitored session types
                    // This prevents "Monitor All" from enabling Practice sessions if the user only wants Race/Qualifying
                    if (shouldMonitor && EventPartDetector.IsMotorsport(league.Sport))
                    {
                        if (!EventPartDetector.IsMotorsportSessionMonitored(evt.Title, league.Name, league.MonitoredSessionTypes))
                        {
                            shouldMonitor = false;
                        }
                    }

                    evt.Monitored = shouldMonitor;

                    if (shouldMonitor)
                    {
                        // When toggling ON: Set to league's default parts (Option A - always use default, forget custom)
                        evt.MonitoredParts = league.MonitoredParts;
                    }
                    else
                    {
                        // When toggling OFF: Clear parts (unmonitor everything)
                        evt.MonitoredParts = null;
                    }

                    evt.LastUpdate = DateTime.UtcNow;
                }

                await db.SaveChangesAsync();

                logger.LogInformation("[SEASON TOGGLE] Successfully updated {EventCount} events", events.Count);

                return Results.Ok(new
                {
                    message = $"Successfully {(monitored ? "monitored" : "unmonitored")} {events.Count} events in season {season}",
                    eventsUpdated = events.Count
                });
            }
        );

        return routeGroup;
    }
}
