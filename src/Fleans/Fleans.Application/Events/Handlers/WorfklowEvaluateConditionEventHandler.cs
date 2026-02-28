using Fleans.Application.Conditions;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public partial class WorfklowEvaluateConditionEventHandler : Grain, IWorfklowEvaluateConditionEventHandler, IAsyncObserver<EvaluateConditionEvent>
{

    private readonly ILogger<WorfklowEvaluateConditionEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;

    public WorfklowEvaluateConditionEventHandler(ILogger<WorfklowEvaluateConditionEventHandler> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(EvaluateConditionEvent));
        var stream = streamProvider.GetStream<EvaluateConditionEvent>(streamId);

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

    public async Task OnNextAsync(EvaluateConditionEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.ActivityId);

        LogHandlingConditionEvent(item.ActivityId, item.SequenceFlowId);

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);

        try
        {
            var expressionEvaluator = _grainFactory.GetGrain<IConditionExpressionEvaluatorGrain>(0);
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(item.ActivityInstanceId);

            var variables = await workflowInstance.GetVariables(await activityInstance.GetVariablesStateId());

            var result = await expressionEvaluator.Evaluate(item.Condition, variables);

            await workflowInstance.CompleteConditionSequence(item.ActivityId, item.SequenceFlowId, result);

            LogConditionResult(item.ActivityId, item.SequenceFlowId, result);
        }
        catch (Exception ex)
        {
            LogConditionEvaluationFailed(ex, item.ActivityId);
            await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
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

    public void Ping()
    {

    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Handling condition event for activity {ActivityId}, sequence {SequenceFlowId}")]
    private partial void LogHandlingConditionEvent(string activityId, string sequenceFlowId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Debug, Message = "Condition evaluation result for activity {ActivityId} and sequence flow {SequenceFlowId}: {Result}")]
    private partial void LogConditionResult(string activityId, string sequenceFlowId, bool result);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Error, Message = "Condition evaluation failed for activity {ActivityId}")]
    private partial void LogConditionEvaluationFailed(Exception ex, string activityId);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Condition event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "Condition event stream error")]
    private partial void LogStreamError(Exception ex);
}
