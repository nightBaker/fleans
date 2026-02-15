using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Application.Scripts;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public partial class WorkflowExecuteScriptEventHandler : Grain, IWorkflowExecuteScriptEventHandler, IAsyncObserver<ExecuteScriptEvent>
{
    private readonly ILogger<WorkflowExecuteScriptEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;

    public WorkflowExecuteScriptEventHandler(ILogger<WorkflowExecuteScriptEventHandler> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(ExecuteScriptEvent));
        var stream = streamProvider.GetStream<ExecuteScriptEvent>(streamId);

        await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(ExecuteScriptEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.ActivityId);

        LogHandlingScriptEvent(item.ActivityId);

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);

        try
        {
            var scriptExecutor = _grainFactory.GetGrain<IScriptExecutorGrain>(0);
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(item.ActivityInstanceId);

            var variables = await workflowInstance.GetVariables(await activityInstance.GetVariablesStateId());

            var result = await scriptExecutor.Execute(item.Script, variables, item.ScriptFormat);

            await workflowInstance.CompleteActivity(item.ActivityId, result);
        }
        catch (Exception ex)
        {
            LogScriptExecutionFailed(ex, item.ActivityId);
            await workflowInstance.FailActivity(item.ActivityId, ex);
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

    [LoggerMessage(EventId = 4010, Level = LogLevel.Information, Message = "Handling script event for activity {ActivityId}")]
    private partial void LogHandlingScriptEvent(string activityId);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Error, Message = "Script execution failed for activity {ActivityId}")]
    private partial void LogScriptExecutionFailed(Exception ex, string activityId);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Information, Message = "Script event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4013, Level = LogLevel.Error, Message = "Script event stream error")]
    private partial void LogStreamError(Exception ex);
}
