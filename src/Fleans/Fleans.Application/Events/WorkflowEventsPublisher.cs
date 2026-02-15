using Fleans.Application.Events.Handlers;
using Fleans.Application.Grains;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Utilities;
using System.IO;

namespace Fleans.Application.Events;

[StatelessWorker]
public partial class WorkflowEventsPublisher : Grain, IEventPublisher
{
    private IStreamProvider _streamProvider = null!;
    private readonly ILogger<WorkflowEventsPublisher> _logger;

    public const string StreamProvider = "StreamProvider";
    public const string StreamNameSpace = "events";

    public WorkflowEventsPublisher(ILogger<WorkflowEventsPublisher> logger)
    {
        _logger = logger;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _streamProvider = this.GetStreamProvider(StreamProvider);

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Publish(IDomainEvent domainEvent)
    {
        LogPublishing(domainEvent.GetType().Name);

        switch (domainEvent)
        {
            case EvaluateConditionEvent evaluateConditionEvent:

                var streamId = StreamId.Create(StreamNameSpace, nameof(EvaluateConditionEvent));
                var stream = _streamProvider.GetStream<EvaluateConditionEvent>(streamId);
                await stream.OnNextAsync(evaluateConditionEvent);
                break;
            case ExecuteScriptEvent executeScriptEvent:

                var scriptStreamId = StreamId.Create(StreamNameSpace, nameof(ExecuteScriptEvent));
                var scriptStream = _streamProvider.GetStream<ExecuteScriptEvent>(scriptStreamId);
                await scriptStream.OnNextAsync(executeScriptEvent);
                break;
            default:
                var defaultStreamId = StreamId.Create(StreamNameSpace, nameof(IDomainEvent));
                var defaultStream = _streamProvider.GetStream<IDomainEvent>(defaultStreamId);
                await defaultStream.OnNextAsync(domainEvent);
                break;
        }

    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Debug, Message = "Publishing {EventType}")]
    private partial void LogPublishing(string eventType);
}
