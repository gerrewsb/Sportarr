using Microsoft.EntityFrameworkCore;
using Serilog;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using System.Text.Json;

namespace Sportarr.Endpoints;

public static class CustomFormatEndpoints
{
    public static WebApplication MapCustomFormatEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/customformat")
            .MapGetCustomFormatEndpoints()
            .MapCreateCustomFormatEndpoint()
            .MapUpdateCustomFormatEndpoint()
            .MapDeleteCustomFormatEndpoint();

        return app;
    }

    private static RouteGroupBuilder MapGetCustomFormatEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/", async (SportarrDbContext db) =>
        {
            var formats = await db.CustomFormats.ToListAsync();
            return Results.Ok(formats);
        });

        routeGroup.MapGet("/{id}", async (int id, SportarrDbContext db) =>
        {
            var format = await db.CustomFormats.FindAsync(id);
            return format == null ? Results.NotFound() : Results.Ok(format);
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapCreateCustomFormatEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/", async (CustomFormat format, SportarrDbContext db, CustomFormatMatchCache cfCache) =>
        {
            format.Created = DateTime.UtcNow;
            db.CustomFormats.Add(format);
            await db.SaveChangesAsync();
            cfCache.InvalidateAll(); // Invalidate CF match cache
            return Results.Ok(format);
        });

        routeGroup.MapPost("/import", async (JsonElement jsonData, SportarrDbContext db, ILogger<Program> logger, CustomFormatMatchCache cfCache) =>
        {
            try
            {
                // Extract required fields
                if (!jsonData.TryGetProperty("name", out var nameElement))
                {
                    return Results.BadRequest(new { error = "JSON must include 'name' field" });
                }

                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    return Results.BadRequest(new { error = "Name cannot be empty" });
                }

                // Check if format with same name already exists
                var existingFormat = await db.CustomFormats.FirstOrDefaultAsync(cf => cf.Name == name);
                if (existingFormat != null)
                {
                    return Results.Conflict(new { error = $"Custom format '{name}' already exists", existingId = existingFormat.Id });
                }

                var format = new CustomFormat
                {
                    Name = name,
                    Created = DateTime.UtcNow
                };

                // Optional: includeCustomFormatWhenRenaming
                if (jsonData.TryGetProperty("includeCustomFormatWhenRenaming", out var renamingElement))
                {
                    format.IncludeCustomFormatWhenRenaming = renamingElement.GetBoolean();
                }

                // Parse specifications
                if (jsonData.TryGetProperty("specifications", out var specsElement) && specsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var specElement in specsElement.EnumerateArray())
                    {
                        var spec = new FormatSpecification
                        {
                            Name = specElement.TryGetProperty("name", out var specName) ? specName.GetString() ?? "" : "",
                            Implementation = specElement.TryGetProperty("implementation", out var impl) ? impl.GetString() ?? "" : "",
                            Negate = specElement.TryGetProperty("negate", out var negate) && negate.GetBoolean(),
                            Required = specElement.TryGetProperty("required", out var required) && required.GetBoolean(),
                            Fields = new Dictionary<string, object>()
                        };

                        // Parse fields - handle both Sonarr format and simple format
                        if (specElement.TryGetProperty("fields", out var fieldsElement))
                        {
                            if (fieldsElement.ValueKind == JsonValueKind.Object)
                            {
                                // Simple format: { "value": "pattern" }
                                foreach (var field in fieldsElement.EnumerateObject())
                                {
                                    spec.Fields[field.Name] = field.Value.ValueKind switch
                                    {
                                        JsonValueKind.String => field.Value.GetString() ?? "",
                                        JsonValueKind.Number => field.Value.GetDouble(),
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => field.Value.ToString()
                                    };
                                }
                            }
                            else if (fieldsElement.ValueKind == JsonValueKind.Array)
                            {
                                // Sonarr format: [ { "name": "value", "value": "pattern" } ]
                                foreach (var fieldObj in fieldsElement.EnumerateArray())
                                {
                                    if (fieldObj.TryGetProperty("name", out var fieldName) &&
                                        fieldObj.TryGetProperty("value", out var fieldValue))
                                    {
                                        var key = fieldName.GetString() ?? "";
                                        spec.Fields[key] = fieldValue.ValueKind switch
                                        {
                                            JsonValueKind.String => fieldValue.GetString() ?? "",
                                            JsonValueKind.Number => fieldValue.GetDouble(),
                                            JsonValueKind.True => true,
                                            JsonValueKind.False => false,
                                            _ => fieldValue.ToString()
                                        };
                                    }
                                }
                            }
                        }

                        format.Specifications.Add(spec);
                    }
                }

                // Get default score from trash_scores if present
                int? defaultScore = null;
                if (jsonData.TryGetProperty("trash_scores", out var scoresElement) &&
                    scoresElement.TryGetProperty("default", out var defaultScoreElement))
                {
                    defaultScore = defaultScoreElement.GetInt32();
                }

                db.CustomFormats.Add(format);
                await db.SaveChangesAsync();
                cfCache.InvalidateAll(); // Invalidate CF match cache

                logger.LogInformation("[CUSTOM FORMAT] Imported format '{Name}' with {SpecCount} specifications (default score: {Score})",
                    format.Name, format.Specifications.Count, defaultScore ?? 0);

                return Results.Ok(new
                {
                    id = format.Id,
                    name = format.Name,
                    specifications = format.Specifications.Count,
                    defaultScore = defaultScore,
                    message = "Custom format imported successfully"
                });
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "[CUSTOM FORMAT] Invalid JSON in import request");
                return Results.BadRequest(new { error = "Invalid JSON format", details = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[CUSTOM FORMAT] Error importing custom format");
                return Results.Problem("Failed to import custom format");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapUpdateCustomFormatEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPut("/{id}", async (int id, CustomFormat format, SportarrDbContext db, ILogger<Program> logger, CustomFormatMatchCache cfCache) =>
        {
            try
            {
                var existing = await db.CustomFormats.FindAsync(id);

                if (existing == null) return Results.NotFound();

                // If this is a synced format, mark it as customized to prevent auto-sync overwriting changes
                bool syncPaused = false;

                if (existing.IsSynced && !existing.IsCustomized)
                {
                    existing.IsCustomized = true;
                    syncPaused = true;
                    Log.Information("[Custom Format] Marked '{Name}' as customized - TRaSH auto-sync paused for this format", existing.Name);
                }

                existing.Name = format.Name;
                existing.IncludeCustomFormatWhenRenaming = format.IncludeCustomFormatWhenRenaming;
                existing.Specifications = format.Specifications;
                existing.LastModified = DateTime.UtcNow;

                await db.SaveChangesAsync();
                cfCache.InvalidateAll(); // Invalidate CF match cache

                // Return sync status info so UI can show appropriate message
                return Results.Ok(new
                {
                    format = existing,
                    syncPaused = syncPaused,
                    message = syncPaused ? "TRaSH auto-sync paused for this format. Import from TRaSH Guides to re-enable." : null
                });
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogError(ex, "[CUSTOM FORMAT] Concurrency error updating format {Id}", id);
                return Results.Conflict(new { error = "Resource was modified by another client. Please refresh and try again." });
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapDeleteCustomFormatEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/{id}", async (int id, SportarrDbContext db, CustomFormatMatchCache cfCache) =>
        {
            var format = await db.CustomFormats.FindAsync(id);

            if (format == null) 
                return Results.NotFound();

            db.CustomFormats.Remove(format);
            await db.SaveChangesAsync();
            cfCache.InvalidateAll(); // Invalidate CF match cache
            return Results.Ok();
        });

        return routeGroup;
    }
}
