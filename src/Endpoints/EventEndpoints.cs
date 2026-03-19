using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Services;

namespace Sportarr.Endpoints;

public static class EventEndpoints
{
    public static WebApplication MapEventEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/event")
            .MapEventPartEndpoint();

        return app;
    }

    /// <summary>
    /// e.g., UFC Fight Night events don't show "Early Prelims" option
    /// </summary>
    /// <param name="routeGroup"></param>
    private static RouteGroupBuilder MapEventPartEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/{eventId:int}/parts", async (int eventId, SportarrDbContext db) =>
        {
            var evt = await db.Events
                .Include(e => e.League)
                .FirstOrDefaultAsync(e => e.Id == eventId);
            
            if (evt == null)
                return Results.NotFound(new { error = "Event not found" });

            var sport = evt.Sport ?? "Fighting";
            var leagueName = evt.League?.Name;
            var segments = EventPartDetector.GetSegmentDefinitions(sport, evt.Title, leagueName);

            return Results.Ok(new
            {
                parts = segments,
                isFightNightStyle = EventPartDetector.IsFightNightStyleEvent(evt.Title, leagueName)
            });
        });

        return routeGroup;
    }
}
