using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Net.Http.Headers;
using Fleans.Application.ExternalTasks.Protocol;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Fleans.ExternalTasks.Sdk;

/// <summary>
/// The worker process's side of the external-task worker protocol (<c>external_task_worker.proto</c>):
/// keeps one <c>Subscribe</c> stream open to the engine for all registered task types
/// (re-subscribing after <see cref="ExternalTaskWorkerOptions.ReconnectDelay"/> when it drops), runs each
/// assigned task on its <see cref="ExternalTaskHandler"/> — at most
/// <see cref="ExternalTaskWorkerOptions.MaxConcurrentTasks"/> at a time — and reports the outcome with
/// <c>CompleteTask</c> / <c>FailTask</c> / <c>DeferTask</c>.
///
/// A task whose token is cancelled (the engine sent a cancellation, the stream dropped, or the
/// host is stopping) is not reported: the engine has already given up on it, or does once the
/// stream closes, and delivers it again.
/// </summary>
internal sealed partial class ExternalTaskWorkerService : BackgroundService
{
    private static readonly TimeSpan[] ReportRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<string, ExternalTaskHandlerDescriptor> _handlers;
    private readonly ExternalTaskWorkerOptions _options;
    private readonly ILogger<ExternalTaskWorkerService> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();

    public ExternalTaskWorkerService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ExternalTaskHandlerDescriptor> handlers,
        IOptions<ExternalTaskWorkerOptions> options,
        ILogger<ExternalTaskWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _handlers = handlers.ToDictionary(p => p.TaskType, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_handlers.Count == 0)
        {
            LogNoHandlers();
            return;
        }

        using var channel = CreateChannel();
        var client = new ExternalTaskWorker.ExternalTaskWorkerClient(channel);
        using var slots = new SemaphoreSlim(_options.MaxConcurrentTasks);
        var request = BuildSubscribeRequest();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    try
                    {
                        using var call = client.Subscribe(request, cancellationToken: connection.Token);
                        var connected = false;
                        await foreach (var command in call.ResponseStream.ReadAllAsync(connection.Token))
                        {
                            if (!connected)
                            {
                                connected = true;
                                LogConnected(_options.EngineUrl!, _options.WorkerName, string.Join(", ", _handlers.Keys));
                            }
                            Handle(command, client, slots, connection.Token);
                        }
                        LogStreamEnded();
                    }
                    catch (Exception) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogDisconnected(ex, _options.EngineUrl!, _options.ReconnectDelay);
                    }
                    finally
                    {
                        // The engine abandons this stream's unreported tasks and delivers them again:
                        // stop working on them.
                        await connection.CancelAsync();
                    }
                }

                try
                {
                    await Task.Delay(_options.ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await Task.WhenAll(_tasks.Keys);
        }
    }

    private void Handle(WorkerCommand command, ExternalTaskWorker.ExternalTaskWorkerClient client, SemaphoreSlim slots, CancellationToken connectionToken)
    {
        switch (command.CommandCase)
        {
            case WorkerCommand.CommandOneofCase.Task:
                var run = RunAsync(command.Task, client, slots, connectionToken);
                _tasks.TryAdd(run, 0);
                _ = run.ContinueWith(t => _tasks.TryRemove(t, out _), TaskScheduler.Default);
                break;
            case WorkerCommand.CommandOneofCase.Cancel:
                if (_running.TryGetValue(command.Cancel.TaskToken, out var cts))
                {
                    LogCancelRequested(command.Cancel.TaskToken);
                    cts.Cancel();
                }
                break;
        }
    }

    private async Task RunAsync(
        TaskAssignment task, ExternalTaskWorker.ExternalTaskWorkerClient client, SemaphoreSlim slots, CancellationToken connectionToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _running[task.TaskToken] = cts;
        try
        {
            await slots.WaitAsync(cts.Token);
            try
            {
                var outcome = await ExecuteTaskAsync(task, cts.Token);
                if (outcome is not null)
                    await ReportAsync(client, task, outcome, connectionToken);
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LogTaskCancelled(task.ActivityId, task.TaskType);
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, task.ActivityId, task.TaskType);
        }
        finally
        {
            _running.TryRemove(task.TaskToken, out _);
        }
    }

    /// <summary>The request to report, or <c>null</c> when the task was cancelled (not reported).</summary>
    private async Task<object?> ExecuteTaskAsync(TaskAssignment task, CancellationToken cancellationToken)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["WorkflowInstanceId"] = task.WorkflowInstanceId,
            ["ActivityId"] = task.ActivityId,
            ["ActivityInstanceId"] = task.ActivityInstanceId,
        });

        if (!TryGetHandler(task.TaskType, out var descriptor))
        {
            LogUnknownTaskType(task.TaskType);
            return Failure(task, "500", $"Worker '{_options.WorkerName}' has no handler for task type '{task.TaskType}'.");
        }

        LogTaskStarted(task.ActivityId, task.TaskType);
        try
        {
            var inputs = new Dictionary<string, object?>(ParseObject(task.InputsJson), StringComparer.Ordinal);
            var variables = ParseObject(task.VariablesJson);
            var context = new ExternalTaskContext(
                Guid.Parse(task.WorkflowInstanceId),
                task.WorkflowId,
                task.HasProcessDefinitionId ? task.ProcessDefinitionId : null,
                Guid.Parse(task.ActivityInstanceId),
                task.ActivityId,
                task.TaskType,
                task.HasBusinessId ? task.BusinessId : null);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = (ExternalTaskHandler)scope.ServiceProvider.GetRequiredService(descriptor.HandlerType);
            var result = await handler.ExecuteAsync(inputs, variables, context, cancellationToken);

            if (ReferenceEquals(result, ExternalTaskHandler.Deferred))
            {
                LogTaskDeferred(task.ActivityId, task.TaskType);
                return new DeferTaskRequest { TaskToken = task.TaskToken };
            }

            LogTaskCompleted(task.ActivityId, task.TaskType);
            return new CompleteTaskRequest
            {
                TaskToken = task.TaskToken,
                ResultJson = JsonConvert.SerializeObject(result ?? new Dictionary<string, object?>()),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalTaskFailedException ex)
        {
            LogTaskFailed(ex, task.ActivityId, task.TaskType, ex.Code);
            return Failure(task, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            LogTaskFailed(ex, task.ActivityId, task.TaskType, "500");
            return Failure(task, "500", ex.Message);
        }
    }

    /// <summary>The handler of the exact type, else the one registered for its <c>scheme:*</c> wildcard.</summary>
    private bool TryGetHandler(string taskType, [NotNullWhen(true)] out ExternalTaskHandlerDescriptor? descriptor)
    {
        if (_handlers.TryGetValue(taskType, out descriptor))
            return true;
        var colon = taskType.IndexOf(':');
        return colon > 0 && _handlers.TryGetValue(taskType[..(colon + 1)] + "*", out descriptor);
    }

    private static FailTaskRequest Failure(TaskAssignment task, string code, string message) =>
        new() { TaskToken = task.TaskToken, Code = code, Message = message };

    /// <summary>
    /// Reports with a few retries on transport failures. Gives up when the connection drops: the
    /// engine then abandons the task and delivers it again.
    /// </summary>
    private async Task ReportAsync(
        ExternalTaskWorker.ExternalTaskWorkerClient client, TaskAssignment task, object outcome, CancellationToken connectionToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                switch (outcome)
                {
                    case CompleteTaskRequest complete:
                        await client.CompleteTaskAsync(complete, cancellationToken: connectionToken);
                        break;
                    case DeferTaskRequest defer:
                        await client.DeferTaskAsync(defer, cancellationToken: connectionToken);
                        break;
                    default:
                        await client.FailTaskAsync((FailTaskRequest)outcome, cancellationToken: connectionToken);
                        break;
                }
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
            {
                LogNotWaiting(task.ActivityId, task.TaskType);
                return;
            }
            catch (RpcException ex) when (attempt < ReportRetryDelays.Length && IsTransient(ex.StatusCode) && !connectionToken.IsCancellationRequested)
            {
                LogReportRetrying(ex, task.ActivityId, attempt + 1);
                await Task.Delay(ReportRetryDelays[attempt], connectionToken);
            }
        }
    }

    private static bool IsTransient(StatusCode code) =>
        code is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal or StatusCode.ResourceExhausted;

    private static ExpandoObject ParseObject(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ExpandoObject()
            : JsonConvert.DeserializeObject<ExpandoObject>(json) ?? new ExpandoObject();

    private SubscribeRequest BuildSubscribeRequest()
    {
        var request = new SubscribeRequest { WorkerName = _options.WorkerName };
        foreach (var handler in _handlers.Values)
        {
            var registration = new TaskTypeRegistration { TaskType = handler.TaskType };
            if (handler.DisplayName is not null)
                registration.DisplayName = handler.DisplayName;
            if (handler.ParameterSchema is not null)
                registration.ParameterSchema = ToProto(handler.ParameterSchema);
            if (handler.Category is not null)
                registration.Category = handler.Category;
            registration.Visibility = handler.Visibility == ExternalTaskVisibility.Private
                ? TaskVisibility.Private
                : TaskVisibility.Public;
            registration.AllowedProcessKeys.AddRange(handler.AllowedProcessKeys ?? []);
            registration.AllVariables = handler.Variables.IncludeAll;
            registration.VariableNames.AddRange(handler.Variables.VariableNames);
            request.TaskTypes.Add(registration);
        }
        return request;
    }

    private static ParameterSchema ToProto(ExternalTaskParameterSchema schema)
    {
        var proto = new ParameterSchema();
        foreach (var p in schema.Parameters)
        {
            var spec = new ParameterSpec { Name = p.Name, Type = ToProto(p.Type), Required = p.Required };
            if (p.DisplayName is not null) spec.DisplayName = p.DisplayName;
            if (p.Description is not null) spec.Description = p.Description;
            if (p.DefaultValue is not null) spec.DefaultValue = p.DefaultValue;
            if (p.ItemType is { } itemType) spec.ItemType = ToProto(itemType);
            proto.Parameters.Add(spec);
        }
        return proto;
    }

    private static ParameterType ToProto(ExternalTaskParameterType type) => type switch
    {
        ExternalTaskParameterType.String => ParameterType.String,
        ExternalTaskParameterType.Integer => ParameterType.Integer,
        ExternalTaskParameterType.Boolean => ParameterType.Boolean,
        ExternalTaskParameterType.Expression => ParameterType.Expression,
        ExternalTaskParameterType.MultilineString => ParameterType.MultilineString,
        ExternalTaskParameterType.List => ParameterType.List,
        ExternalTaskParameterType.Map => ParameterType.Map,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private GrpcChannel CreateChannel()
    {
        HttpMessageHandler handler = _options.HttpHandler ?? new SocketsHttpHandler
        {
            // Long-lived stream: keep the HTTP/2 connection alive through idle NATs and proxies.
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
        };
        if (_options.AccessTokenProvider is not null || _options.AccessToken is not null)
            handler = new BearerTokenHandler(_options) { InnerHandler = handler };
        if (_options.UseGrpcWeb)
            handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, handler) { HttpVersion = System.Net.HttpVersion.Version11 };

        return GrpcChannel.ForAddress(_options.EngineUrl!, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = _options.HttpHandler is null,
            // Inputs, variables and results are JSON documents; let them be as large as the
            // engine allows (Fleans:Variables:MaxSizeBytes).
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null,
        });
    }

    /// <summary>Adds <c>Authorization: Bearer …</c> — over plain HTTP too, unlike gRPC call credentials.</summary>
    private sealed class BearerTokenHandler(ExternalTaskWorkerOptions options) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = options.AccessTokenProvider is not null
                ? await options.AccessTokenProvider(cancellationToken)
                : options.AccessToken;
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [LoggerMessage(EventId = 14570, Level = LogLevel.Information,
        Message = "Connected to the Fleans engine at {EngineUrl} as worker '{WorkerName}' for task types {TaskTypes}")]
    private partial void LogConnected(Uri engineUrl, string workerName, string taskTypes);

    [LoggerMessage(EventId = 14571, Level = LogLevel.Warning,
        Message = "Lost the connection to the Fleans engine at {EngineUrl}; re-subscribing in {Delay}")]
    private partial void LogDisconnected(Exception ex, Uri engineUrl, TimeSpan delay);

    [LoggerMessage(EventId = 14572, Level = LogLevel.Information,
        Message = "The engine closed the task stream; re-subscribing")]
    private partial void LogStreamEnded();

    [LoggerMessage(EventId = 14573, Level = LogLevel.Warning,
        Message = "No external-task handler is registered (AddExternalTaskHandler); the worker does not connect")]
    private partial void LogNoHandlers();

    [LoggerMessage(EventId = 14574, Level = LogLevel.Information,
        Message = "Running external task for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogTaskStarted(string activityId, string taskType);

    [LoggerMessage(EventId = 14575, Level = LogLevel.Information,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) completed")]
    private partial void LogTaskCompleted(string activityId, string taskType);

    [LoggerMessage(EventId = 14576, Level = LogLevel.Error,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) failed with code {Code}")]
    private partial void LogTaskFailed(Exception ex, string activityId, string taskType, string code);

    [LoggerMessage(EventId = 14577, Level = LogLevel.Information,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) cancelled — not reported; the engine delivers it again")]
    private partial void LogTaskCancelled(string activityId, string taskType);

    [LoggerMessage(EventId = 14578, Level = LogLevel.Information,
        Message = "The engine asked to cancel task {TaskToken}")]
    private partial void LogCancelRequested(string taskToken);

    [LoggerMessage(EventId = 14579, Level = LogLevel.Information,
        Message = "The engine no longer waits for the external task of activity {ActivityId} (taskType={TaskType}); its outcome was dropped")]
    private partial void LogNotWaiting(string activityId, string taskType);

    [LoggerMessage(EventId = 14580, Level = LogLevel.Warning,
        Message = "Reporting the external task of activity {ActivityId} failed (attempt {Attempt}); retrying")]
    private partial void LogReportRetrying(Exception ex, string activityId, int attempt);

    [LoggerMessage(EventId = 14581, Level = LogLevel.Error,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) could not be run or reported")]
    private partial void LogUnexpected(Exception ex, string activityId, string taskType);

    [LoggerMessage(EventId = 14582, Level = LogLevel.Error,
        Message = "The engine assigned task type '{TaskType}', which this worker has no handler for")]
    private partial void LogUnknownTaskType(string taskType);

    [LoggerMessage(EventId = 14583, Level = LogLevel.Information,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) deferred — its outcome is reported out of band")]
    private partial void LogTaskDeferred(string activityId, string taskType);
}
