using Serilog;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Extensions;

public static class WebApplicationExtensions
{
    /// <summary>
    /// URL Base support for reverse proxy setups (e.g., /sportarr)
    /// Must be configured early in the pipeline, before routing
    /// </summary>
    /// <param name="app"></param>
    public static async Task<WebApplication> ConfigureUrlBase(this WebApplication app)
    {
        var configService = app.Services.GetRequiredService<IConfigService>();
        var config = await configService.GetConfigAsync();
        var configuredUrlBase = config.UrlBase?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(configuredUrlBase))
        {
            // Ensure proper formatting: starts with /, no trailing /
            if (!configuredUrlBase.StartsWith('/'))
                configuredUrlBase = '/' + configuredUrlBase;

            configuredUrlBase = configuredUrlBase.TrimEnd('/');

            Log.Information("[URL Base] Configured URL base: {UrlBase}", configuredUrlBase);

            // UsePathBase strips the URL base from incoming request paths
            // e.g., /sportarr/api/leagues becomes /api/leagues
            app.UsePathBase(configuredUrlBase);
        }

        return app;
    }

    public static WebApplication ConfigureStaticFilesMiddleWare(this WebApplication app)
    {
        // Configure static files (UI from wwwroot)
        // For URL base support, we need to inject the urlBase into index.html
        // and rewrite asset paths to include the base
        app.Use(async (context, next) =>
        {
            // Serve index.html with urlBase injection for SPA routes
            var path = context.Request.Path.Value ?? "";

            // Check if this is a request that should serve index.html (SPA fallback)
            // Skip API routes, static assets, and other special endpoints
            var isApiOrAsset = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/assets", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/initialize.json", StringComparison.OrdinalIgnoreCase) 
                || path.StartsWith("/ping", StringComparison.OrdinalIgnoreCase) 
                || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) 
                || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) 
                || path.Contains(".");  // Has file extension (e.g., .js, .css, .svg)

            if (!isApiOrAsset)
            {
                // Serve index.html with urlBase injected
                var webRootPath = app.Environment.WebRootPath;
                var indexPath = Path.Combine(webRootPath, "index.html");

                if (File.Exists(indexPath))
                {
                    var html = await File.ReadAllTextAsync(indexPath);

                    // Get the configured URL base
                    var configService = context.RequestServices.GetRequiredService<IConfigService>();
                    var config = await configService.GetConfigAsync();
                    var urlBase = config.UrlBase?.Trim() ?? string.Empty;

                    if (!string.IsNullOrEmpty(urlBase))
                    {
                        if (!urlBase.StartsWith('/'))
                            urlBase = '/' + urlBase;

                        urlBase = urlBase.TrimEnd('/');

                        // Inject urlBase script before the first script tag
                        // This sets window.Sportarr.urlBase BEFORE main.tsx runs
                        var urlBaseScript = $@"<script>window.Sportarr = window.Sportarr || {{}}; window.Sportarr.urlBase = '{urlBase}';</script>";
                        html = html.Replace("<script", urlBaseScript + "<script");

                        // Rewrite asset paths to include urlBase
                        // /assets/ -> /sportarr/assets/
                        // /logo.svg -> /sportarr/logo.svg
                        html = html.Replace("href=\"/", $"href=\"{urlBase}/");
                        html = html.Replace("src=\"/", $"src=\"{urlBase}/");
                    }

                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync(html);
                    return;
                }
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        return app;
    }
}
