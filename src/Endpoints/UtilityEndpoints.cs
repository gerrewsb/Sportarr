using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Endpoints;

public static class UtilityEndpoints
{
    public static WebApplication MapUtilityEndpoints(this WebApplication app)
    {
        app.MapInitializeEndpoint()
            .MapPingEndpoint()
            .MapHealthEndpoint();

        return app;
    }

    private static WebApplication MapInitializeEndpoint(this WebApplication app)
    {
        // Initialize endpoint (for frontend) - keep for SPA compatibility
        app.MapGet("/initialize.json", async (IConfigService configService) =>
        {
            // Get API key from config.xml (same source that authentication uses)
            var config = await configService.GetConfigAsync();
            // Ensure urlBase is properly formatted (starts with / if not empty, no trailing /)
            var urlBase = config.UrlBase?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(urlBase))
            {
                if (!urlBase.StartsWith('/'))
                    urlBase = '/' + urlBase;

                urlBase = urlBase.TrimEnd('/');
            }

            return Results.Json(new
            {
                apiRoot = "", // Empty since all API routes already start with /api
                apiKey = config.ApiKey,
                release = Api.Version.GetFullVersion(),
                version = Api.Version.GetFullVersion(),
                instanceName = "Sportarr",
                theme = "auto",
                branch = "main",
                analytics = false,
                userHash = Guid.NewGuid().ToString("N")[..8],
                urlBase = urlBase,
                isProduction = !app.Environment.IsDevelopment()
            });
        });

        return app;
    }

    private static WebApplication MapPingEndpoint(this WebApplication app)
    {
        // Health check
        app.MapGet("/ping", () => Results.Ok("pong"));

        return app;
    }

    /// <summary>
    /// Map built-in health checks endpoint (provides detailed health status)
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    private static WebApplication MapHealthEndpoint(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var result = new
                {
                    status = report.Status.ToString(),
                    totalDuration = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        duration = e.Value.Duration.TotalMilliseconds,
                        description = e.Value.Description,
                        data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                        exception = e.Value.Exception?.Message
                    })
                };
                await context.Response.WriteAsJsonAsync(result);
            }
        });

        return app;
    }
}
