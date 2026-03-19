using Serilog;
using Sportarr.Api.Models;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Endpoints;

public static class TaskEndpoints
{
    public static WebApplication MapTaskEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/task")
            .MapGetTaskEndpoints()
            .MapQueueTaskEndpoint()
            .MapDeleteTaskEndpoint()
            .MapCleanupTasksEndpoint();

        return app;
    }

    private static RouteGroupBuilder MapGetTaskEndpoints(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapGet("/", async (ITaskService taskService, int? pageSize) =>
        {
            try
            {
                var tasks = await taskService.GetAllTasksAsync(pageSize);
                return Results.Ok(tasks);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TASK API] Error getting tasks");
                return Results.Problem("Error getting tasks");
            }
        });

        routeGroup.MapGet("/{id:int}", async (int id, ITaskService taskService) =>
        {
            try
            {
                var task = await taskService.GetTaskAsync(id);
                return task is null ? Results.NotFound() : Results.Ok(task);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TASK API] Error getting task {TaskId}", id);
                return Results.Problem("Error getting task");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapQueueTaskEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/", async (ITaskService taskService, TaskRequest request) =>
        {
            try
            {
                var task = await taskService.QueueTaskAsync(
                    request.Name,
                    request.CommandName,
                    request.Priority ?? 0,
                    request.Body
                );
                return Results.Created($"/api/task/{task.Id}", task);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TASK API] Error queueing task");
                return Results.Problem("Error queueing task");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapDeleteTaskEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapDelete("/{id:int}", async (int id, ITaskService taskService) =>
        {
            try
            {
                var success = await taskService.CancelTaskAsync(id);
                if (!success)
                {
                    return Results.NotFound(new { message = "Task not found or cannot be cancelled" });
                }
                return Results.Ok(new { message = "Task cancelled successfully" });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TASK API] Error cancelling task {TaskId}", id);
                return Results.Problem("Error cancelling task");
            }
        });

        return routeGroup;
    }

    private static RouteGroupBuilder MapCleanupTasksEndpoint(this RouteGroupBuilder routeGroup)
    {
        routeGroup.MapPost("/cleanup", async (ITaskService taskService, int? keepCount) =>
        {
            try
            {
                await taskService.CleanupOldTasksAsync(keepCount ?? 100);
                return Results.Ok(new { message = "Old tasks cleaned up successfully" });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TASK API] Error cleaning up tasks");
                return Results.Problem("Error cleaning up tasks");
            }
        });

        return routeGroup;
    }
}
