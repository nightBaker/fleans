using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
[CorePlacement]
public partial class WorkflowExecuteCustomTaskEventHandler : Grain, IWorkflowExecuteCustomTaskEventHandler, IAsyncObserver<ExecuteCustomTaskEvent>
{
    private readonly ILogger<WorkflowExecuteCustomTaskEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;
    private readonly CustomTaskCallProviderRegistry _registry;

    public WorkflowExecuteCustomTaskEventHandler(
        ILogger<WorkflowExecuteCustomTaskEventHandler> logger,
        IGrainFactory grainFactory,
        CustomTaskCallProviderRegistry registry)
    {
        _logger = logger;
        _grainFactory = grainFactory;
        _registry = registry;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(ExecuteCustomTaskEvent));
        var stream = streamProvider.GetStream<ExecuteCustomTaskEvent>(streamId);

        var handles = await stream.GetAllSubscriptionHandles();
        if (handles is { Count: > 0 })
        {
            foreach (var handle in handles)
                await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
        }
        else
        {
            await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
        }

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(ExecuteCustomTaskEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.ActivityId);

        LogHandlingCustomTaskEvent(item.ActivityId, item.TaskType);

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);

        try
        {
            var variables = await workflowInstance.GetVariables(item.VariablesId);

            if (!_registry.TryGetGrainInterface(item.TaskType, out var grainInterface))
                throw new CustomTaskFailedActivityException(400, $"No provider registered for task type '{item.TaskType}'");

            var resolved = new Dictionary<string, object?>();
            foreach (var im in item.InputMappings)
                resolved[im.Target] = MappingResolver.Resolve(im.Source, (IDictionary<string, object?>)variables);

            var provider = (ICustomTaskCallProvider)_grainFactory.GetGrain(grainInterface!, item.ActivityInstanceId);
            await provider.ExecuteAsync(resolved, variables);

            // Output pass: project from `resolved` (which the provider mutated to add __response, etc.)
            // into a fresh ExpandoObject for CompleteActivity.
            var outputs = new ExpandoObject();
            var outputsDict = (IDictionary<string, object?>)outputs;
            foreach (var om in item.OutputMappings)
                outputsDict[om.Target] = MappingResolver.Resolve(om.Source, resolved);

            await workflowInstance.CompleteActivity(item.ActivityId, item.ActivityInstanceId, outputs);
        }
        catch (Exception ex)
        {
            LogCustomTaskExecutionFailed(ex, item.ActivityId, item.TaskType);
            try
            {
                await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
            }
            catch (Exception failEx)
            {
                LogFailActivityFailed(failEx, item.ActivityId);
                throw; // Let stream provider retry — domain idempotency guards handle duplicates
            }
        }
    }

    public Task OnCompletedAsync()
    {
        LogStreamCompleted();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 4020, Level = LogLevel.Information, Message = "Handling custom-task event for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogHandlingCustomTaskEvent(string activityId, string taskType);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Error, Message = "Custom-task execution failed for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogCustomTaskExecutionFailed(Exception ex, string activityId, string taskType);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Information, Message = "Custom-task event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4023, Level = LogLevel.Error, Message = "Custom-task event stream error")]
    private partial void LogStreamError(Exception ex);

    [LoggerMessage(EventId = 4024, Level = LogLevel.Critical, Message = "FailActivity call itself failed for activity {ActivityId} — workflow may be stalled")]
    private partial void LogFailActivityFailed(Exception ex, string activityId);
}
