using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventStreams.EvaluateCompletionConditionStreamNamespace)]
[CorePlacement]
public partial class WorkflowEvaluateCompletionConditionEventHandler : Grain, IAsyncObserver<EvaluateCompletionConditionEvent>
{
    private readonly ILogger<WorkflowEvaluateCompletionConditionEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;

    public WorkflowEvaluateCompletionConditionEventHandler(
        ILogger<WorkflowEvaluateCompletionConditionEventHandler> logger,
        IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventStreams.EvaluateCompletionConditionStreamNamespace, nameof(EvaluateCompletionConditionEvent));
        var stream = streamProvider.GetStream<EvaluateCompletionConditionEvent>(streamId);

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

    public async Task OnNextAsync(EvaluateCompletionConditionEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.HostActivityId);

        LogEvaluatingCompletionCondition(item.HostActivityId, item.HostActivityInstanceId,
            item.NrOfInstances, item.NrOfActiveInstances, item.NrOfCompletedInstances);

        try
        {
            var variables = new ExpandoObject() as IDictionary<string, object?>;
            variables["nrOfInstances"] = (object)item.NrOfInstances;
            variables["nrOfActiveInstances"] = (object)item.NrOfActiveInstances;
            variables["nrOfCompletedInstances"] = (object)item.NrOfCompletedInstances;

            var evaluator = _grainFactory.GetGrain<IConditionExpressionEvaluatorGrain>(0);
            var result = await evaluator.Evaluate(item.Condition, (ExpandoObject)variables);

            LogCompletionConditionResult(item.HostActivityId, item.HostActivityInstanceId, result);

            if (result)
            {
                var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);
                await grain.CompleteMultiInstanceEarly(item.HostActivityId, item.HostActivityInstanceId);
            }
        }
        catch (Exception ex)
        {
            LogCompletionConditionEvaluationFailed(ex, item.HostActivityId, item.HostActivityInstanceId);
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

    // No-op: forces Orleans to activate this grain so the implicit stream subscription starts.
    public void Ping() { }

    [LoggerMessage(EventId = 4030, Level = LogLevel.Information,
        Message = "Evaluating completion condition for host {HostActivityId}, instance {HostActivityInstanceId}: total={NrOfInstances} active={NrOfActiveInstances} completed={NrOfCompletedInstances}")]
    private partial void LogEvaluatingCompletionCondition(string hostActivityId, Guid hostActivityInstanceId, int nrOfInstances, int nrOfActiveInstances, int nrOfCompletedInstances);

    [LoggerMessage(EventId = 4031, Level = LogLevel.Debug,
        Message = "Completion condition result for host {HostActivityId}, instance {HostActivityInstanceId}: {Result}")]
    private partial void LogCompletionConditionResult(string hostActivityId, Guid hostActivityInstanceId, bool result);

    [LoggerMessage(EventId = 4032, Level = LogLevel.Error,
        Message = "Completion condition evaluation failed for host {HostActivityId}, instance {HostActivityInstanceId}")]
    private partial void LogCompletionConditionEvaluationFailed(Exception ex, string hostActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 4033, Level = LogLevel.Information,
        Message = "Completion condition event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4034, Level = LogLevel.Error,
        Message = "Completion condition event stream error")]
    private partial void LogStreamError(Exception ex);
}
