using System.Dynamic;
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
public partial class WorkflowEvaluateActivationConditionEventHandler : Grain, IWorkflowEvaluateActivationConditionEventHandler, IAsyncObserver<EvaluateActivationConditionEvent>
{
    private readonly ILogger<WorkflowEvaluateActivationConditionEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;

    public WorkflowEvaluateActivationConditionEventHandler(
        ILogger<WorkflowEvaluateActivationConditionEventHandler> logger,
        IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(EvaluateActivationConditionEvent));
        var stream = streamProvider.GetStream<EvaluateActivationConditionEvent>(streamId);

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

    public async Task OnNextAsync(EvaluateActivationConditionEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.ActivityId);

        LogHandlingActivationConditionEvent(item.ActivityId, item.ActivityInstanceId, item.NrOfToken);

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);

        try
        {
            var variables = await workflowInstance.GetVariables(item.VariablesId);

            // Inject _nroftoken into the merged variables so expressions can reference _context._nroftoken
            ((IDictionary<string, object?>)variables)["_nroftoken"] = (object)item.NrOfToken;

            var expressionEvaluator = _grainFactory.GetGrain<IConditionExpressionEvaluatorGrain>(0);
            var result = await expressionEvaluator.Evaluate(item.Condition, variables);

            LogActivationConditionResult(item.ActivityId, item.ActivityInstanceId, result, item.NrOfToken);

            await workflowInstance.CompleteActivationCondition(item.ActivityId, item.ActivityInstanceId, result);
        }
        catch (Exception ex)
        {
            LogActivationConditionEvaluationFailed(ex, item.ActivityId, item.ActivityInstanceId);
            try
            {
                await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
            }
            catch (Exception failEx)
            {
                LogFailActivityFailed(failEx, item.ActivityId);
                throw;
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

    public void Ping() { }

    [LoggerMessage(EventId = 4020, Level = LogLevel.Information,
        Message = "Handling activation condition for activity {ActivityId}, instance {ActivityInstanceId}, nrOfToken={NrOfToken}")]
    private partial void LogHandlingActivationConditionEvent(string activityId, Guid activityInstanceId, int nrOfToken);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Debug,
        Message = "Activation condition result for activity {ActivityId}, instance {ActivityInstanceId}: {Result} (nrOfToken={NrOfToken})")]
    private partial void LogActivationConditionResult(string activityId, Guid activityInstanceId, bool result, int nrOfToken);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Error,
        Message = "Activation condition evaluation failed for activity {ActivityId}, instance {ActivityInstanceId}")]
    private partial void LogActivationConditionEvaluationFailed(Exception ex, string activityId, Guid activityInstanceId);

    [LoggerMessage(EventId = 4023, Level = LogLevel.Information,
        Message = "Activation condition event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4024, Level = LogLevel.Error,
        Message = "Activation condition event stream error")]
    private partial void LogStreamError(Exception ex);

    [LoggerMessage(EventId = 4025, Level = LogLevel.Critical,
        Message = "FailActivity call itself failed for activity {ActivityId} — workflow may be stalled")]
    private partial void LogFailActivityFailed(Exception ex, string activityId);
}
