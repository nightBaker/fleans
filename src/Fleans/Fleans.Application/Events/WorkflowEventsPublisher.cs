using Fleans.Application.Abstractions.Events;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Grains;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events;

/// <summary>
/// Publishes the domain events that leave the workflow instance through a stream — today the
/// custom-task requests, to the external-task stream served to gRPC workers by
/// <see cref="ExternalTaskDispatcherGrain"/> (#979, #1013). Script tasks are dispatched straight to the script
/// executor grain and conditions are evaluated in the instance, so neither comes through here.
/// </summary>
[StatelessWorker]
public partial class WorkflowEventsPublisher : Grain, IEventPublisher
{
    private IStreamProvider _streamProvider = null!;
    private readonly ILogger<WorkflowEventsPublisher> _logger;

    // Stream-namespace constants live in Fleans.Application.Abstractions (Events/WorkflowEventStreams.cs).
    // The aliases below preserve in-tree call sites that still reference the publisher type.
    public const string StreamProvider = WorkflowEventStreams.StreamProvider;
    public const string StreamNameSpace = WorkflowEventStreams.StreamNameSpace;

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
            case ExecuteCustomTaskEvent externalTask:
                await PublishExternalTask(externalTask);
                break;
            case WorkflowActivityExecutedEvent:
                // Raised for every executed activity and subscribed to by nothing: dropped without a
                // log line. As a warning it was ~28 log lines per instance on the hot path (#868).
                break;
            default:
                // No subscriber exists for the default stream; logging and dropping surfaces
                // unhandled event types at the publisher rather than fanning them out to a
                // dead-letter stream.
                LogUnknownEventType(domainEvent.GetType().FullName ?? "(null)");
                break;
        }

    }

    private async Task PublishExternalTask(ExecuteCustomTaskEvent task)
    {
        // Keyed by ActivityInstanceId: one dispatcher activation per task, so parallel branches of
        // one instance run concurrently (#815).
        var streamId = StreamId.Create(WorkflowEventStreams.ExternalTaskStreamNamespace, task.ActivityInstanceId.ToString("D"));
        await _streamProvider.GetStream<ExecuteCustomTaskEvent>(streamId).OnNextAsync(task);
        LogExternalTaskPublished(task.ActivityId, task.ActivityInstanceId, task.TaskType);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Debug, Message = "Publishing {EventType}")]
    private partial void LogPublishing(string eventType);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "WorkflowEventsPublisher received unhandled event type {EventTypeName}; dropping. Add a case to Publish if intentional.")]
    private partial void LogUnknownEventType(string eventTypeName);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug,
        Message = "External task {ActivityId} ({ActivityInstanceId}, type {TaskType}) published to the external-task stream")]
    private partial void LogExternalTaskPublished(string activityId, Guid activityInstanceId, string taskType);
}
