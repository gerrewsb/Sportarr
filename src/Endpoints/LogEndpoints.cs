using System.IO.Abstractions;

namespace Sportarr.Endpoints;

public static class LogEndpoints
{
    public static WebApplication MapLogEndpoints(this WebApplication app, string logsPath)
    {
        app.MapGroup("/api/log/file")
            .MapLogListEndpoint(logsPath)
            .MapLogContentEndpoint(logsPath)
            .MapLogDownloadEndpoint(logsPath);

        return app;
    }

    private static RouteGroupBuilder MapLogListEndpoint(this RouteGroupBuilder routeGroup, string logsPath)
    {
        routeGroup.MapGet("/", (ILogger<Program> logger, IFileSystem fileSystem) =>
        {
            try
            {
                var logFiles = fileSystem.Directory.GetFiles(logsPath, "*.txt")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new
                    {
                        filename = fileSystem.Path.GetFileName(f.FullName),
                        lastWriteTime = f.LastWriteTime,
                        size = f.Length
                    })
                    .ToList();

                logger.LogDebug("[LOG FILES] Listing {Count} log files", logFiles.Count);
                return Results.Ok(logFiles);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LOG FILES] Error listing log files");
                return Results.Problem("Error listing log files");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapLogContentEndpoint(this RouteGroupBuilder routeGroup, string logsPath)
    {
        routeGroup.MapGet("/content", (string filename, ILogger<Program> logger, IFileSystem fileSystem) =>
        {
            try
            {
                if (string.IsNullOrEmpty(filename))
                {
                    return Results.BadRequest(new { message = "Filename is required" });
                }

                // Sanitize filename to prevent directory traversal
                filename = fileSystem.Path.GetFileName(filename);
                var logFilePath = fileSystem.Path.Combine(logsPath, filename);

                if (!File.Exists(logFilePath))
                {
                    logger.LogDebug("[LOG FILES] File not found: {Filename}", filename);
                    return Results.NotFound(new { message = "Log file not found" });
                }

                logger.LogDebug("[LOG FILES] Reading log file: {Filename}", filename);

                // Read with FileShare.ReadWrite to allow reading while Serilog is writing
                string content;
                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream))
                {
                    content = reader.ReadToEnd();
                }

                return Results.Ok(new
                {
                    filename = filename,
                    content = content,
                    lastWriteTime = fileSystem.File.GetLastWriteTime(logFilePath),
                    size = new FileInfo(logFilePath).Length
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LOG FILES] Error reading log file: {Filename}", filename);
                return Results.Problem("Error reading log file");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapLogDownloadEndpoint(this RouteGroupBuilder routeGroup, string logsPath)
    {
        routeGroup.MapGet("/download", (string filename, ILogger<Program> logger, IFileSystem fileSystem) =>
        {
            try
            {
                if (string.IsNullOrEmpty(filename))
                {
                    return Results.BadRequest(new { message = "Filename is required" });
                }

                // Sanitize filename to prevent directory traversal
                filename = fileSystem.Path.GetFileName(filename);
                var logFilePath = fileSystem.Path.Combine(logsPath, filename);

                if (!File.Exists(logFilePath))
                {
                    logger.LogDebug("[LOG FILES] File not found for download: {Filename}", filename);
                    return Results.NotFound(new { message = "Log file not found" });
                }

                logger.LogDebug("[LOG FILES] Downloading log file: {Filename}", filename);

                // Read with FileShare.ReadWrite to allow reading while Serilog is writing
                byte[] fileBytes;
                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var memoryStream = new MemoryStream())
                {
                    fileStream.CopyTo(memoryStream);
                    fileBytes = memoryStream.ToArray();
                }

                return Results.File(fileBytes, "text/plain", filename);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LOG FILES] Error downloading log file: {Filename}", filename);
                return Results.Problem("Error downloading log file");
            }
        });

        return routeGroup;
    }
}
