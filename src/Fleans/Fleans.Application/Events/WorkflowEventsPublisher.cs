using Fleans.Application.Abstractions.Events;
using Fleans.Application.Grains;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events;

/// <summary>
/// Publishes the domain events that leave the workflow instance through a stream — today the
/// custom-task requests to plugin handlers. Script tasks are dispatched straight to the script
/// executor grain and conditions are evaluated in the instance, so neither comes through here.
/// </summary>
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
            case ExecuteCustomTaskEvent executeCustomTaskEvent:

                // Partition the namespace by TaskType so each plugin's subscriber grain class
                // only sees its own traffic (eliminates the N×M filter-after-deliver fanout), and
                // shard the stream by WorkflowInstanceId so cross-instance traffic spreads across
                // queues. See docs/conventions/streaming.md "Custom-task per-type stream namespace".
                var customTaskNs = WorkflowEventStreams.GetExecuteCustomTaskNamespace(executeCustomTaskEvent.TaskType);
                var customTaskStreamId = StreamId.Create(customTaskNs, executeCustomTaskEvent.WorkflowInstanceId.ToString("D"));
                var customTaskStream = _streamProvider.GetStream<ExecuteCustomTaskEvent>(customTaskStreamId);
                await customTaskStream.OnNextAsync(executeCustomTaskEvent);
                break;
            default:
                // No subscriber exists for the default stream; logging and dropping surfaces
                // unhandled event types at the publisher rather than fanning them out to a
                // dead-letter stream.
                LogUnknownEventType(domainEvent.GetType().FullName ?? "(null)");
                break;
        }

    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Debug, Message = "Publishing {EventType}")]
    private partial void LogPublishing(string eventType);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "WorkflowEventsPublisher received unhandled event type {EventTypeName}; dropping. Add a case to Publish if intentional.")]
    private partial void LogUnknownEventType(string eventTypeName);
}
