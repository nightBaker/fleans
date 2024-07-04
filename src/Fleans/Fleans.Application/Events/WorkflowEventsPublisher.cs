using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Utilities;
using System.IO;

namespace Fleans.Application.Events;

public class WorkflowEventsPublisher : Grain, IEventPublisher
{
    private IStreamProvider _streamProvider = null!;

    public const string StreamProvider = "StreamProvider";
    public const string StreamNameSpace = "events";

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _streamProvider = this.GetStreamProvider(StreamProvider);

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Publish(IDomainEvent domainEvent)
    {                                        
        switch (domainEvent)
        {
            case EvaluateConditionEvent evaluateConditionEvent:
                
                var streamId = StreamId.Create(StreamNameSpace, nameof(EvaluateConditionEvent));
                var stream = _streamProvider.GetStream<EvaluateConditionEvent>(streamId);
                await stream.OnNextAsync(evaluateConditionEvent);
                break;
            default:
                var defaultStreamId = StreamId.Create(StreamNameSpace, nameof(IDomainEvent));
                var defaultStream = _streamProvider.GetStream<IDomainEvent>(defaultStreamId);
                await defaultStream.OnNextAsync(domainEvent);
                break;
        }

    }    
}

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public class ReceiverGrainTest : Grain, IWorkflowEventsHandler
{

    private readonly ILogger<ReceiverGrainTest> _logger;

    public ReceiverGrainTest(ILogger<ReceiverGrainTest> logger)
    {
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(EvaluateConditionEvent));
        var stream = streamProvider.GetStream<EvaluateConditionEvent>(streamId);

        await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
        _logger.LogInformation("OnCompletedAsync()");
        await base.OnActivateAsync(cancellationToken);
    }

    public Task OnNextAsync(EvaluateConditionEvent item, StreamSequenceToken? token = null)
    {
        _logger.LogInformation("OnNextAsync({Item}{Token})", item, token != null ? token.ToString() : "null");

        return Task.CompletedTask;
    }

    public Task OnCompletedAsync()
    {
        _logger.LogInformation("OnCompletedAsync()");
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogInformation(ex, "OnErrorAsync()");

        return Task.CompletedTask;
    }    
}
