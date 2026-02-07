using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public class WorkflowExecuteScriptEventHandler : Grain, IWorkflowExecuteScriptEventHandler, IAsyncObserver<ExecuteScriptEvent>
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
        _logger.LogInformation("OnNextAsync({Item}{Token})", item, token != null ? token.ToString() : "null");

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstance>(item.WorkflowInstanceId);

        try
        {
            var scriptExecutor = _grainFactory.GetGrain<IScriptExecutorGrain>(0);
            var activityInstance = _grainFactory.GetGrain<IActivityInstance>(item.ActivityInstanceId);

            var variables = await workflowInstance.GetVariables(await activityInstance.GetVariablesStateId());

            var result = await scriptExecutor.Execute(item.Script, variables, item.ScriptFormat);

            await workflowInstance.CompleteActivity(item.ActivityId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script execution failed for activity {ActivityId}", item.ActivityId);
            await workflowInstance.FailActivity(item.ActivityId, ex);
        }
    }

    public Task OnCompletedAsync()
    {
        _logger.LogInformation("OnCompletedAsync()");
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "OnErrorAsync()");
        return Task.CompletedTask;
    }
}
