using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Endpoints;

public static class DelayProfileEndpoints
{
    public static WebApplication MapDelayProfileEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/delayprofile")
            .MapGetEndpoints()
            .MapPostEndpoints()
            .MapPutEndpoints()
            .MapDeleteEndpoints();

        return app;
    }

    private static RouteGroupBuilder MapGetEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/", async (SportarrDbContext db) =>
        {
            var profiles = await db.DelayProfiles.OrderBy(d => d.Order).ToListAsync();
            return Results.Ok(profiles);
        });

        routeGroup.MapGet("/{id}", async (int id, SportarrDbContext db) =>
        {
            var profile = await db.DelayProfiles.FindAsync(id);
            return profile == null ? Results.NotFound() : Results.Ok(profile);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapPostEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/", async (DelayProfile profile, SportarrDbContext db) =>
        {
            profile.Created = DateTime.UtcNow;
            db.DelayProfiles.Add(profile);
            await db.SaveChangesAsync();
            return Results.Ok(profile);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapPutEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPut("/{id}", async (int id, DelayProfile profile, SportarrDbContext db, ILogger<Program> logger) =>
        {
            try
            {
                var existing = await db.DelayProfiles.FindAsync(id);
                if (existing == null) return Results.NotFound();

                existing.Order = profile.Order;
                existing.PreferredProtocol = profile.PreferredProtocol;
                existing.UsenetDelay = profile.UsenetDelay;
                existing.TorrentDelay = profile.TorrentDelay;
                existing.BypassIfHighestQuality = profile.BypassIfHighestQuality;
                existing.BypassIfAboveCustomFormatScore = profile.BypassIfAboveCustomFormatScore;
                existing.MinimumCustomFormatScore = profile.MinimumCustomFormatScore;
                existing.Tags = profile.Tags;
                existing.LastModified = DateTime.UtcNow;

                await db.SaveChangesAsync();
                return Results.Ok(existing);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogError(ex, "[DELAY PROFILE] Concurrency error updating profile {Id}", id);
                return Results.Conflict(new { error = "Resource was modified by another client. Please refresh and try again." });
            }
        });

        routeGroup.MapPut("/reorder", async (List<int> profileIds, SportarrDbContext db) =>
        {
            for (int i = 0; i < profileIds.Count; i++)
            {
                var profile = await db.DelayProfiles.FindAsync(profileIds[i]);
                if (profile != null)
                {
                    profile.Order = i + 1;
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapDeleteEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/{id}", async (int id, SportarrDbContext db) =>
        {
            var profile = await db.DelayProfiles.FindAsync(id);
            if (profile == null) return Results.NotFound();

            db.DelayProfiles.Remove(profile);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        return routeGroup;
    }
}
