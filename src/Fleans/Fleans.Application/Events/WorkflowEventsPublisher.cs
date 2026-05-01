using Fleans.Application.Grains;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events;

[StatelessWorker]
public partial class WorkflowEventsPublisher : Grain, IEventPublisher
{
    private IStreamProvider _streamProvider = null!;
    private readonly ILogger<WorkflowEventsPublisher> _logger;

    // Keep in sync with FleanStreamingExtensions.StreamProviderName in Fleans.ServiceDefaults
    public const string StreamProvider = "StreamProvider";

    /// <summary>
    /// Shared namespace for engine-internal events (script, condition, activation-condition,
    /// default fallback). Custom-task events use a dedicated namespace below to keep
    /// plugin handlers from being implicit-subscribed to engine-internal streams.
    /// </summary>
    public const string StreamNameSpace = "events";

    /// <summary>
    /// Dedicated namespace for <see cref="ExecuteCustomTaskEvent"/>. Plugin handlers
    /// (<c>CustomTaskHandlerBase</c> subclasses) carry
    /// <c>[ImplicitStreamSubscription(ExecuteCustomTaskStreamNamespace)]</c> so Orleans
    /// only activates them on custom-task events. Sharing the engine "events" namespace
    /// caused every implicit-subscriber grain class to be activated for every event type
    /// — Orleans then logs "got an item for subscription …, but I don't have any
    /// subscriber for that stream. Dropping on the floor." on each cross-event delivery.
    /// </summary>
    public const string ExecuteCustomTaskStreamNamespace = "events.ExecuteCustomTaskEvent";

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
            case EvaluateActivationConditionEvent evaluateActivationConditionEvent:
                var activationStreamId = StreamId.Create(StreamNameSpace, nameof(EvaluateActivationConditionEvent));
                var activationStream = _streamProvider.GetStream<EvaluateActivationConditionEvent>(activationStreamId);
                await activationStream.OnNextAsync(evaluateActivationConditionEvent);
                break;
            case ExecuteScriptEvent executeScriptEvent:

                var scriptStreamId = StreamId.Create(StreamNameSpace, nameof(ExecuteScriptEvent));
                var scriptStream = _streamProvider.GetStream<ExecuteScriptEvent>(scriptStreamId);
                await scriptStream.OnNextAsync(executeScriptEvent);
                break;
            case ExecuteCustomTaskEvent executeCustomTaskEvent:

                var customTaskStreamId = StreamId.Create(ExecuteCustomTaskStreamNamespace, nameof(ExecuteCustomTaskEvent));
                var customTaskStream = _streamProvider.GetStream<ExecuteCustomTaskEvent>(customTaskStreamId);
                await customTaskStream.OnNextAsync(executeCustomTaskEvent);
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
