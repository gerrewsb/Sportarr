using Microsoft.AspNetCore.Mvc;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Endpoints;

public static class TrashGuidesEndpoints
{
    public static WebApplication MapTrashGuidesEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/trash")
            .MapGetEndpoints()
            .MapPostEndpoints()
            .MapPutEndpoints()
            .MapDeleteEndpoints();

        return app;
    }

    private static RouteGroupBuilder MapGetEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/status", async (TrashGuideSyncService trashService) =>
        {
            try
            {
                var status = await trashService.GetSyncStatusAsync();
                return Results.Ok(status);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to get TRaSH sync status: {ex.Message}");
            }
        });

        routeGroup.MapGet("/customformats", async (TrashGuideSyncService trashService, bool sportRelevantOnly = true) =>
        {
            try
            {
                var formats = await trashService.GetAvailableCustomFormatsAsync(sportRelevantOnly);
                return Results.Ok(formats);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to get TRaSH custom formats: {ex.Message}");
            }
        });

        routeGroup.MapGet("/scoresets", () =>
        {
            return Results.Ok(TrashScoreSets.DisplayNames);
        });

        routeGroup.MapGet("/preview", async (TrashGuideSyncService trashService, bool sportRelevantOnly = true) =>
        {
            try
            {
                var preview = await trashService.PreviewSyncAsync(sportRelevantOnly);
                return Results.Ok(preview);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to preview sync: {ex.Message}");
            }
        });

        routeGroup.MapGet("/profiles", async (TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] GET /api/trash/profiles - Fetching available profile templates");
                var profiles = await trashService.GetAvailableQualityProfilesAsync();
                logger.LogInformation("[TRaSH API] Returning {Count} profile templates", profiles.Count);

                if (profiles.Count == 0)
                {
                    logger.LogWarning("[TRaSH API] No profile templates returned - check TRaSH Sync logs for details");
                }
                else
                {
                    foreach (var profile in profiles.Take(3))
                    {
                        logger.LogInformation("[TRaSH API] Profile: {Name} (TrashId: {TrashId})", profile.Name, profile.TrashId);
                    }
                }

                return Results.Ok(profiles);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to get profile templates");
                return Results.Problem($"Failed to get profile templates: {ex.Message}");
            }
        });

        routeGroup.MapGet("/settings", async (TrashGuideSyncService trashService) =>
        {
            try
            {
                var settings = await trashService.GetSyncSettingsAsync();
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to get sync settings: {ex.Message}");
            }
        });

        routeGroup.MapGet("/naming-presets", (TrashGuideSyncService trashService, bool enableMultiPartEpisodes = true) =>
        {
            try
            {
                var presets = trashService.GetNamingPresets(enableMultiPartEpisodes);
                return Results.Ok(presets);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to get naming presets: {ex.Message}");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapPostEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/sync", async (TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Starting full sport-relevant sync");
                var result = await trashService.SyncAllSportCustomFormatsAsync();

                if (result.Success)
                {
                    logger.LogInformation("[TRaSH API] Sync completed: {Created} created, {Updated} updated",
                        result.Created, result.Updated);
                }
                else
                {
                    logger.LogWarning("[TRaSH API] Sync failed: {Error}", result.Error);
                }

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Sync failed");
                return Results.Problem($"TRaSH sync failed: {ex.Message}");
            }
        });

        routeGroup.MapPost("/sync/selected", async (List<string> trashIds, TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Syncing {Count} selected custom formats", trashIds.Count);
                var result = await trashService.SyncCustomFormatsByIdsAsync(trashIds);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Selected sync failed");
                return Results.Problem($"TRaSH sync failed: {ex.Message}");
            }
        });

        routeGroup.MapPost("/apply-scores/{profileId}", async (int profileId, TrashGuideSyncService trashService, ILogger<Program> logger, string scoreSet = "default") =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Applying TRaSH scores to profile {ProfileId} using score set '{ScoreSet}'",
                    profileId, scoreSet);
                var result = await trashService.ApplyTrashScoresToProfileAsync(profileId, scoreSet, forceUpdate: true);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to apply scores to profile {ProfileId}", profileId);
                return Results.Problem($"Failed to apply TRaSH scores: {ex.Message}");
            }
        });

        routeGroup.MapPost("/reset/{formatId}", async (int formatId, TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Resetting custom format {FormatId} to TRaSH defaults", formatId);
                var success = await trashService.ResetCustomFormatToTrashDefaultAsync(formatId);

                if (success)
                {
                    return Results.Ok(new { message = "Custom format reset to TRaSH defaults" });
                }
                else
                {
                    return Results.NotFound(new { error = "Custom format not found or not synced from TRaSH" });
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to reset format {FormatId}", formatId);
                return Results.Problem($"Failed to reset custom format: {ex.Message}");
            }
        });

        routeGroup.MapPost("/profiles/create", async (TrashGuideSyncService trashService, ILogger<Program> logger, string trashId, string? customName = null) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Creating profile from template {TrashId}", trashId);
                var (success, error, profileId) = await trashService.CreateProfileFromTemplateAsync(trashId, customName);

                if (success)
                    return Results.Ok(new { success = true, profileId });
                else
                    return Results.BadRequest(new { success = false, error });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to create profile from template");
                return Results.Problem($"Failed to create profile: {ex.Message}");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapPutEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPut("/settings", async (TrashSyncSettings settings, TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Saving sync settings (AutoSync: {AutoSync}, Interval: {Interval}h)",
                    settings.EnableAutoSync, settings.AutoSyncIntervalHours);
                await trashService.SaveSyncSettingsAsync(settings);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to save sync settings");
                return Results.Problem($"Failed to save settings: {ex.Message}");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapDeleteEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/api/trash/formats", async (TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Deleting all synced custom formats");
                var result = await trashService.DeleteAllSyncedFormatsAsync();
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to delete synced formats");
                return Results.Problem($"Failed to delete formats: {ex.Message}");
            }
        });

        routeGroup.MapDelete("/api/trash/formats/selected", async ([FromBody] List<string> trashIds, TrashGuideSyncService trashService, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("[TRaSH API] Deleting {Count} selected synced formats by trash ID", trashIds.Count);
                var result = await trashService.DeleteSyncedFormatsByTrashIdsAsync(trashIds);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TRaSH API] Failed to delete formats");
                return Results.Problem($"Failed to delete formats: {ex.Message}");
            }
        });

        return routeGroup;
    }
}
