using Fleans.Application.Conditions;
using Fleans.Application.Events;
using Fleans.Domain;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public class WorfklowEvaluateConditionEventHandler : Grain, IWorfklowEvaluateConditionEventHandler, IAsyncObserver<EvaluateConditionEvent>
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

        await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(EvaluateConditionEvent item, StreamSequenceToken? token = null)
    {
        _logger.LogInformation("OnNextAsync({Item}{Token})", item, token != null ? token.ToString() : "null");

        var expressionEvaluator = _grainFactory.GetGrain<IConditionExpressionEvaluaterGrain>(0);
        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstance>(item.WorkflowInstanceId);
        var activityInstance = _grainFactory.GetGrain<IActivityInstance>(item.ActivityInstanceId);

        var variables = await workflowInstance.GetVariables(await activityInstance.GetVariablesStateId());

        var result = await expressionEvaluator.Evaluate(item.Condition, variables);

        await workflowInstance.CompleteConditionSequence(item.ActivityId, item.SequenceFlowId, result);
        
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

    public void Ping()
    {

    }
}
