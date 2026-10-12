using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Maps the Camunda 7 <c>/external-task</c> REST resource so existing workers (camunda-external-task-client
/// for Java / JS, or hand-written HTTP clients) only need their base URL changed.
/// </summary>
public static class CamundaExternalTaskEndpoints
{
    /// <summary>
    /// Maps <c>{basePath}/external-task/*</c>. Returns the route group so the host can add
    /// authorization (<c>.RequireAuthorization()</c>), rate limiting, etc.
    /// </summary>
    public static RouteGroupBuilder MapCamundaExternalTaskApi(
        this IEndpointRouteBuilder endpoints, string basePath = "/engine-rest")
    {
        var group = endpoints.MapGroup($"{basePath.TrimEnd('/')}/external-task")
            .WithTags("Camunda External Task")
            .AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (ExternalTaskApiException ex)
                {
                    return Json(new CamundaErrorDto(ex.Type, ex.Message), ex.StatusCode);
                }
            });

        group.MapPost("/fetchAndLock", async (FetchExternalTasksDto body, ExternalTaskService service, HttpContext http) =>
            Json(await service.FetchAndLock(body, http.RequestAborted)));

        group.MapGet("/", async (ExternalTaskService service, [AsParameters] ExternalTaskQueryParameters p) =>
            Json(await service.Query(p.ToQuery())));

        group.MapGet("/count", async (ExternalTaskService service, [AsParameters] ExternalTaskQueryParameters p) =>
            Json(new CountResultDto(await service.Count(p.ToQuery()))));

        group.MapGet("/topic-names", async (ExternalTaskService service,
                bool? withLockedTasks, bool? withUnlockedTasks, bool? withRetriesLeft) =>
            Json(await service.TopicNames(withLockedTasks ?? false, withUnlockedTasks ?? false, withRetriesLeft ?? false)));

        group.MapGet("/{id}", async (string id, ExternalTaskService service) =>
            Json(await service.Get(id)));

        group.MapPost("/{id}/complete", async (string id, CompleteExternalTaskDto body, ExternalTaskService service) =>
        {
            await service.Complete(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/failure", async (string id, ExternalTaskFailureDto body, ExternalTaskService service) =>
        {
            await service.HandleFailure(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/bpmnError", async (string id, ExternalTaskBpmnErrorDto body, ExternalTaskService service) =>
        {
            await service.HandleBpmnError(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/extendLock", async (string id, ExtendLockDto body, ExternalTaskService service) =>
        {
            await service.ExtendLock(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/lock", async (string id, LockExternalTaskDto body, ExternalTaskService service) =>
        {
            await service.Lock(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/unlock", async (string id, ExternalTaskService service) =>
        {
            await service.Unlock(id);
            return Results.NoContent();
        });

        group.MapPut("/{id}/retries", async (string id, RetriesDto body, ExternalTaskService service) =>
        {
            await service.SetRetries(id, body);
            return Results.NoContent();
        });

        group.MapPut("/{id}/priority", async (string id, PriorityDto body, ExternalTaskService service) =>
        {
            await service.SetPriority(id, body);
            return Results.NoContent();
        });

        return group;
    }

    /// <summary>
    /// Camunda answers with a bare <c>application/json</c> content type, and the official JS client
    /// only parses bodies whose content type matches it exactly — ASP.NET's default
    /// <c>application/json; charset=utf-8</c> makes it treat the body as raw bytes.
    /// </summary>
    private static IResult Json(object value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, contentType: "application/json", statusCode: statusCode);

    /// <summary>Query-string filters of <c>GET /external-task</c> (Camunda parameter names).</summary>
    public sealed class ExternalTaskQueryParameters
    {
        public string? ExternalTaskId { get; init; }
        public string? TopicName { get; init; }
        public string? WorkerId { get; init; }
        public bool? Locked { get; init; }
        public bool? NotLocked { get; init; }
        public bool? WithRetriesLeft { get; init; }
        public bool? NoRetriesLeft { get; init; }
        public string? ProcessInstanceId { get; init; }
        public string? ActivityId { get; init; }
        public string? ProcessDefinitionId { get; init; }
        public string? ProcessDefinitionKey { get; init; }
        public int? FirstResult { get; init; }
        public int? MaxResults { get; init; }

        public ExternalTaskQuery ToQuery() => new(
            ExternalTaskId, TopicName, WorkerId,
            Locked ?? false, NotLocked ?? false, WithRetriesLeft ?? false, NoRetriesLeft ?? false,
            ProcessInstanceId, ActivityId, ProcessDefinitionId, ProcessDefinitionKey,
            FirstResult, MaxResults);
    }
}
