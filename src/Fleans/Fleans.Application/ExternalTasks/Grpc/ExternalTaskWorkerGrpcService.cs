using Fleans.Application.CustomTasks;
using System.Dynamic;
using Fleans.Application.ExternalTasks.Protocol;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fleans.Application.ExternalTasks.Grpc;

/// <summary>
/// The engine side of the external-task worker protocol (<c>external_task_worker.proto</c>), hosted
/// by Fleans.Api. A <c>Subscribe</c> stream becomes a <see cref="WorkerConnection"/>: an
/// <see cref="IExternalTaskWorker"/> observer joined to the <see cref="IExternalTaskWorkerPoolGrain"/>
/// of each task type the worker announced (and registered in the catalog), renewed on every
/// heartbeat and removed when the stream closes — at which point the tasks the worker had not
/// reported are abandoned, so their dispatchers let the stream redeliver them.
/// <c>CompleteTask</c> / <c>FailTask</c> hand the outcome to the
/// <see cref="IExternalTaskDispatcherGrain"/> named by the task token.
/// </summary>
public sealed partial class ExternalTaskWorkerGrpcService : ExternalTaskWorker.ExternalTaskWorkerBase
{
    private const int MaxWorkerNameLength = 256;
    private const int MaxTaskTypeLength = 128;

    private readonly IGrainFactory _grainFactory;
    private readonly ExternalTaskWorkerConnections _connections;
    private readonly ExternalTaskOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExternalTaskWorkerGrpcService> _logger;

    public ExternalTaskWorkerGrpcService(
        IGrainFactory grainFactory,
        ExternalTaskWorkerConnections connections,
        IOptions<ExternalTaskOptions> options,
        TimeProvider timeProvider,
        ILogger<ExternalTaskWorkerGrpcService> logger)
    {
        _grainFactory = grainFactory;
        _connections = connections;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override async Task Subscribe(
        SubscribeRequest request, IServerStreamWriter<WorkerCommand> responseStream, ServerCallContext context)
    {
        var registrations = ToRegistrations(request);
        var connection = new WorkerConnection(request.WorkerName, _timeProvider);
        // Orleans holds observers weakly: `connection` stays referenced until the stream closes.
        var worker = _grainFactory.CreateObjectReference<IExternalTaskWorker>(connection);
        _connections.Add(connection);
        var ct = context.CancellationToken;
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeats = null;
        try
        {
            try
            {
                await RenewAsync(connection, worker, registrations);
            }
            catch (Exception ex)
            {
                LogSubscribeFailed(ex, connection.WorkerName);
                throw new RpcException(new Status(StatusCode.Unavailable, "The engine could not register the worker; retry."));
            }

            LogConnected(connection.WorkerName, connection.ConnectionId, string.Join(", ", registrations.Select(r => r.TaskType)));
            heartbeats = HeartbeatAsync(connection, worker, registrations, heartbeatStop.Token);

            await foreach (var command in connection.Commands.ReadAllAsync(ct))
                await responseStream.WriteAsync(command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The worker closed the stream, or this process is shutting down.
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            if (heartbeats is not null)
                await heartbeats;
            _connections.Remove(connection);
            await DisconnectAsync(connection, registrations);
            _grainFactory.DeleteObjectReference<IExternalTaskWorker>(worker);
        }
    }

    public override async Task<CompleteTaskResponse> CompleteTask(CompleteTaskRequest request, ServerCallContext context)
    {
        var (activityInstanceId, dispatchId) = ParseToken(request.TaskToken);
        var result = ParseJsonObject(request.ResultJson, "result_json");

        _connections.Release(request.TaskToken);
        var accepted = await _grainFactory.GetGrain<IExternalTaskDispatcherGrain>(activityInstanceId.ToString("D"))
            .Complete(dispatchId, result);
        if (!accepted)
            throw NotWaiting(request.TaskToken);
        return new CompleteTaskResponse();
    }

    public override async Task<FailTaskResponse> FailTask(FailTaskRequest request, ServerCallContext context)
    {
        var (activityInstanceId, dispatchId) = ParseToken(request.TaskToken);
        var code = string.IsNullOrEmpty(request.Code) ? "500" : request.Code;

        _connections.Release(request.TaskToken);
        var accepted = await _grainFactory.GetGrain<IExternalTaskDispatcherGrain>(activityInstanceId.ToString("D"))
            .Fail(dispatchId, code, request.Message);
        if (!accepted)
            throw NotWaiting(request.TaskToken);
        return new FailTaskResponse();
    }

    private async Task RenewAsync(
        WorkerConnection connection, IExternalTaskWorker worker, IReadOnlyList<CustomTaskRegistration> registrations)
    {
        var catalog = _grainFactory.GetGrain<ICustomTaskCatalogGrain>(0);
        foreach (var registration in registrations)
        {
            await _grainFactory.GetGrain<IExternalTaskWorkerPoolGrain>(ExternalTaskWorkerPoolGrain.KeyFor(registration.TaskType))
                .Join(connection.ConnectionId, worker);
            await catalog.RegisterWorker(registration);
        }
    }

    private async Task HeartbeatAsync(
        WorkerConnection connection, IExternalTaskWorker worker, IReadOnlyList<CustomTaskRegistration> registrations,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                connection.Heartbeat();
                // A task the dispatcher no longer waits for needs no abandoning.
                connection.ForgetAssignedBefore(_timeProvider.GetUtcNow() - _options.ExecutionTimeout - _options.WorkerTimeout);
                try
                {
                    await RenewAsync(connection, worker, registrations);
                }
                catch (Exception ex)
                {
                    LogRenewFailed(ex, connection.WorkerName);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DisconnectAsync(WorkerConnection connection, IReadOnlyList<CustomTaskRegistration> registrations)
    {
        var unreported = connection.Close();
        try
        {
            var catalog = _grainFactory.GetGrain<ICustomTaskCatalogGrain>(0);
            foreach (var registration in registrations)
            {
                await _grainFactory.GetGrain<IExternalTaskWorkerPoolGrain>(ExternalTaskWorkerPoolGrain.KeyFor(registration.TaskType))
                    .Leave(connection.ConnectionId);
                await catalog.UnregisterWorker(registration.TaskType, connection.WorkerName);
            }
        }
        catch (Exception ex)
        {
            // The pools and the catalog drop the worker once its renewals stop.
            LogUnregisterFailed(ex, connection.WorkerName);
        }

        foreach (var token in unreported)
        {
            if (!ExternalTaskToken.TryParse(token, out var activityInstanceId, out var dispatchId))
                continue;
            try
            {
                await _grainFactory.GetGrain<IExternalTaskDispatcherGrain>(activityInstanceId.ToString("D")).Abandon(dispatchId);
            }
            catch (Exception ex)
            {
                // The dispatcher's execution timeout redelivers the task.
                LogAbandonFailed(ex, connection.WorkerName);
            }
        }

        LogDisconnected(connection.WorkerName, connection.ConnectionId, unreported.Count);
    }

    private static List<CustomTaskRegistration> ToRegistrations(SubscribeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkerName) || request.WorkerName.Length > MaxWorkerNameLength)
            throw InvalidArgument($"worker_name must be 1–{MaxWorkerNameLength} characters.");
        if (request.TaskTypes.Count == 0)
            throw InvalidArgument("task_types must name at least one task type.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registrations = new List<CustomTaskRegistration>(request.TaskTypes.Count);
        foreach (var taskType in request.TaskTypes)
        {
            if (string.IsNullOrWhiteSpace(taskType.TaskType) || taskType.TaskType.Length > MaxTaskTypeLength)
                throw InvalidArgument($"task_type must be 1–{MaxTaskTypeLength} characters.");
            if (!seen.Add(taskType.TaskType))
                throw InvalidArgument($"task_type '{taskType.TaskType}' is listed twice.");

            registrations.Add(new CustomTaskRegistration(
                taskType.TaskType,
                taskType.HasDisplayName ? taskType.DisplayName : null,
                taskType.ParameterSchema is null ? null : ToSchema(taskType.TaskType, taskType.ParameterSchema),
                request.WorkerName));
        }
        return registrations;
    }

    private static CustomTaskParameterSchema ToSchema(string taskType, ParameterSchema schema)
    {
        var parameters = new List<CustomTaskParameterSpec>(schema.Parameters.Count);
        foreach (var p in schema.Parameters)
        {
            var type = ToParameterType(p.Type);
            CustomTaskParameterType? itemType = p.HasItemType ? ToParameterType(p.ItemType) : null;
            if (type is CustomTaskParameterType.List or CustomTaskParameterType.Map
                && itemType is null or CustomTaskParameterType.List or CustomTaskParameterType.Map)
            {
                throw InvalidArgument(
                    $"Parameter '{p.Name}' of task type '{taskType}' is a {type}: item_type must name a non-collection type.");
            }
            parameters.Add(new CustomTaskParameterSpec(
                p.Name,
                p.HasDisplayName ? p.DisplayName : null,
                type,
                p.Required,
                p.HasDescription ? p.Description : null,
                p.HasDefaultValue ? p.DefaultValue : null,
                itemType));
        }
        return new CustomTaskParameterSchema(parameters);
    }

    private static CustomTaskParameterType ToParameterType(ParameterType type) => type switch
    {
        ParameterType.String => CustomTaskParameterType.String,
        ParameterType.Integer => CustomTaskParameterType.Integer,
        ParameterType.Boolean => CustomTaskParameterType.Boolean,
        ParameterType.Expression => CustomTaskParameterType.Expression,
        ParameterType.MultilineString => CustomTaskParameterType.MultilineString,
        ParameterType.List => CustomTaskParameterType.List,
        ParameterType.Map => CustomTaskParameterType.Map,
        _ => throw InvalidArgument($"Unknown parameter type {(int)type}."),
    };

    private static (Guid ActivityInstanceId, Guid DispatchId) ParseToken(string token) =>
        ExternalTaskToken.TryParse(token, out var activityInstanceId, out var dispatchId)
            ? (activityInstanceId, dispatchId)
            : throw InvalidArgument("task_token is not a token this engine issued.");

    private static ExpandoObject ParseJsonObject(string json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ExpandoObject();
        try
        {
            return JToken.Parse(json) is JObject
                ? JsonConvert.DeserializeObject<ExpandoObject>(json) ?? new ExpandoObject()
                : throw InvalidArgument($"{field} must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw InvalidArgument($"{field} is not valid JSON: {ex.Message}");
        }
    }

    private static RpcException InvalidArgument(string detail) => new(new Status(StatusCode.InvalidArgument, detail));

    private static RpcException NotWaiting(string token) => new(new Status(StatusCode.FailedPrecondition,
        $"The engine no longer waits for task {token}: it was cancelled, timed out, its worker disconnected, or it was already reported."));

    [LoggerMessage(EventId = 14540, Level = LogLevel.Information,
        Message = "External-task worker '{WorkerName}' connected (connection {ConnectionId}) for task types {TaskTypes}")]
    private partial void LogConnected(string workerName, string connectionId, string taskTypes);

    [LoggerMessage(EventId = 14541, Level = LogLevel.Information,
        Message = "External-task worker '{WorkerName}' disconnected (connection {ConnectionId}); {Unreported} unreported tasks abandoned for redelivery")]
    private partial void LogDisconnected(string workerName, string connectionId, int unreported);

    [LoggerMessage(EventId = 14542, Level = LogLevel.Error,
        Message = "External-task worker '{WorkerName}' could not be registered; its subscription is refused")]
    private partial void LogSubscribeFailed(Exception ex, string workerName);

    [LoggerMessage(EventId = 14543, Level = LogLevel.Warning,
        Message = "Renewing external-task worker '{WorkerName}' failed; retrying on the next heartbeat")]
    private partial void LogRenewFailed(Exception ex, string workerName);

    [LoggerMessage(EventId = 14544, Level = LogLevel.Warning,
        Message = "Unregistering disconnected external-task worker '{WorkerName}' failed; it expires after the worker timeout")]
    private partial void LogUnregisterFailed(Exception ex, string workerName);

    [LoggerMessage(EventId = 14545, Level = LogLevel.Warning,
        Message = "Abandoning a task of disconnected external-task worker '{WorkerName}' failed; the execution timeout redelivers it")]
    private partial void LogAbandonFailed(Exception ex, string workerName);
}
