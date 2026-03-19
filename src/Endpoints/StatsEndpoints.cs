using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Endpoints;

public static class StatsEndpoints
{
    public static WebApplication MapStatsEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/stats")
            .MapStatsEndpoint();

        return app;
    }

    /// <summary>
    /// API: Stats - Provides counts for Homepage widget integration (similar to Sonarr/Radarr)
    /// Returns: wanted (missing events), queued (download queue), leagues count, events count
    /// </summary>
    /// <param name="routeGroup"></param>
    private static RouteGroupBuilder MapStatsEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/", async (SportarrDbContext db) =>
        {
            // Count missing events (monitored but no file)
            var wantedCount = await db.Events
                .Where(e => e.Monitored && !e.HasFile)
                .CountAsync();

            // Count active queue items (downloading, not imported)
            var queuedCount = await db.DownloadQueue
                .Where(dq => dq.Status != DownloadStatus.Imported)
                .CountAsync();

            // Count leagues
            var leagueCount = await db.Leagues.CountAsync();

            // Count total events
            var eventCount = await db.Events.CountAsync();

            // Count monitored events
            var monitoredEventCount = await db.Events
                .Where(e => e.Monitored)
                .CountAsync();

            // Count events with files
            var downloadedEventCount = await db.Events
                .Where(e => e.HasFile)
                .CountAsync();

            // Count total files
            var fileCount = await db.EventFiles.CountAsync();

            return Results.Ok(new
            {
                wanted = wantedCount,
                queued = queuedCount,
                leagues = leagueCount,
                events = eventCount,
                monitored = monitoredEventCount,
                downloaded = downloadedEventCount,
                files = fileCount
            });
        });

        return routeGroup;
    }
}
