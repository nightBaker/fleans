using Fleans.Application.Abstractions.Events;
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

    // Stream-namespace constants live in Fleans.Application.Abstractions
    // (Events/WorkflowEventStreams.cs) so plugin handlers can subscribe without
    // taking a transitive ref on Fleans.Application / Fleans.Domain. The aliases
    // below preserve in-tree call sites that still reference the publisher type.
    public const string StreamProvider = WorkflowEventStreams.StreamProvider;
    public const string StreamNameSpace = WorkflowEventStreams.StreamNameSpace;
    public const string ExecuteCustomTaskStreamNamespace = WorkflowEventStreams.ExecuteCustomTaskStreamNamespace;
    public const string ExecuteScriptStreamNamespace = WorkflowEventStreams.ExecuteScriptStreamNamespace;
    public const string EvaluateConditionStreamNamespace = WorkflowEventStreams.EvaluateConditionStreamNamespace;
    public const string EvaluateActivationConditionStreamNamespace = WorkflowEventStreams.EvaluateActivationConditionStreamNamespace;

    public WorkflowEventsPublisher(ILogger<WorkflowEventsPublisher> logger)
    {
        _logger = logger;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Publish(IDomainEvent domainEvent)
    {
        LogPublishing(domainEvent.GetType().Name);

        switch (domainEvent)
        {
            case EvaluateConditionEvent evaluateConditionEvent:

                var streamId = StreamId.Create(WorkflowEventStreams.EvaluateConditionStreamNamespace, nameof(EvaluateConditionEvent));
                var stream = _streamProvider.GetStream<EvaluateConditionEvent>(streamId);
                await stream.OnNextAsync(evaluateConditionEvent);
                break;
            case EvaluateActivationConditionEvent evaluateActivationConditionEvent:
                var activationStreamId = StreamId.Create(WorkflowEventStreams.EvaluateActivationConditionStreamNamespace, nameof(EvaluateActivationConditionEvent));
                var activationStream = _streamProvider.GetStream<EvaluateActivationConditionEvent>(activationStreamId);
                await activationStream.OnNextAsync(evaluateActivationConditionEvent);
                break;
            case ExecuteScriptEvent executeScriptEvent:

                var scriptStreamId = StreamId.Create(WorkflowEventStreams.ExecuteScriptStreamNamespace, nameof(ExecuteScriptEvent));
                var scriptStream = _streamProvider.GetStream<ExecuteScriptEvent>(scriptStreamId);
                await scriptStream.OnNextAsync(executeScriptEvent);
                break;
            case ExecuteCustomTaskEvent executeCustomTaskEvent:

                var customTaskStreamId = StreamId.Create(WorkflowEventStreams.ExecuteCustomTaskStreamNamespace, nameof(ExecuteCustomTaskEvent));
                var customTaskStream = _streamProvider.GetStream<ExecuteCustomTaskEvent>(customTaskStreamId);
                await customTaskStream.OnNextAsync(executeCustomTaskEvent);
                break;
            default:
                var defaultStreamId = StreamId.Create(WorkflowEventStreams.StreamNameSpace, nameof(IDomainEvent));
                var defaultStream = _streamProvider.GetStream<IDomainEvent>(defaultStreamId);
                await defaultStream.OnNextAsync(domainEvent);
                break;
        }

    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Debug, Message = "Publishing {EventType}")]
    private partial void LogPublishing(string eventType);
}
