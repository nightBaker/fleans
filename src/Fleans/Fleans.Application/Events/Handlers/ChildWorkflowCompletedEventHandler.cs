using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public partial class ChildWorkflowCompletedEventHandler : Grain, IChildWorkflowCompletedEventHandler, IAsyncObserver<ChildWorkflowCompletedEvent>
{
    private readonly ILogger<ChildWorkflowCompletedEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;

    public ChildWorkflowCompletedEventHandler(ILogger<ChildWorkflowCompletedEventHandler> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(ChildWorkflowCompletedEvent));
        var stream = streamProvider.GetStream<ChildWorkflowCompletedEvent>(streamId);

        await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(ChildWorkflowCompletedEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.ParentWorkflowInstanceId, item.ParentActivityId);

        LogHandlingChildCompleted(item.ParentActivityId, item.ParentWorkflowInstanceId);

        var parentWorkflow = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.ParentWorkflowInstanceId);
        await parentWorkflow.OnChildWorkflowCompleted(item.ParentActivityId, item.ChildVariables);
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

    [LoggerMessage(EventId = 4020, Level = LogLevel.Information, Message = "Handling child workflow completed for activity {ActivityId} on parent {ParentWorkflowInstanceId}")]
    private partial void LogHandlingChildCompleted(string activityId, Guid parentWorkflowInstanceId);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Information, Message = "Child workflow completed event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4022, Level = LogLevel.Error, Message = "Child workflow completed event stream error")]
    private partial void LogStreamError(Exception ex);
}
