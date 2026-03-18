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
}
