using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sportarr.Api.Services;

/// <summary>
/// Background service that performs scheduled Event Mapping sync from the Sportarr API.
/// Similar to Sonarr's XEM sync, this runs automatically every 12 hours.
/// </summary>
public class EventMappingSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventMappingSyncBackgroundService> _logger;

    // Sync every 12 hours (like Sonarr's XEM)
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(12);

    // Wait 2 minutes on startup before first sync to let other services initialize
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public EventMappingSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<EventMappingSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Event Mapping Sync] Background service started - syncing every 12 hours");

        // Wait a bit on startup before first sync
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var eventMappingService = scope.ServiceProvider.GetRequiredService<EventMappingService>();

                _logger.LogDebug("[Event Mapping Sync] Starting automatic sync...");

                var result = await eventMappingService.SyncFromApiAsync(fullSync: false);

                if (result.Success)
                {
                    if (result.Added > 0 || result.Updated > 0)
                    {
                        _logger.LogInformation(
                            "[Event Mapping Sync] Completed: {Added} added, {Updated} updated, {Unchanged} unchanged",
                            result.Added, result.Updated, result.Unchanged);
                    }
                    else
                    {
                        _logger.LogDebug("[Event Mapping Sync] No changes detected");
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[Event Mapping Sync] Sync completed with errors: {Errors}",
                        string.Join(", ", result.Errors));
                }

                // Also check status of any pending mapping requests
                var statusUpdates = await eventMappingService.CheckPendingRequestStatusesAsync();
                if (statusUpdates.Count > 0)
                {
                    _logger.LogInformation(
                        "[Event Mapping Sync] {Count} mapping request(s) have been reviewed",
                        statusUpdates.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Event Mapping Sync] Error during automatic sync");
            }

            await Task.Delay(SyncInterval, stoppingToken);
        }

        _logger.LogInformation("[Event Mapping Sync] Background service stopped");
    }
}
