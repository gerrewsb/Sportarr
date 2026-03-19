using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;

namespace Sportarr.Endpoints;

public static class TagEndpoints
{
    public static WebApplication MapTagEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/tag")
            .MapGetTags();

        return app;
    }

    private static RouteGroupBuilder MapGetTags(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/", async (SportarrDbContext db) =>
        {
            var tags = await db.Tags.ToListAsync();
            return Results.Ok(tags);
        });

        return routeGroup;
    }
}
