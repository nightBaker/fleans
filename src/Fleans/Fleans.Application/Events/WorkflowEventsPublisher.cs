using Fleans.Application.Abstractions.Events;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Grains;
using Fleans.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events;

/// <summary>
/// Publishes the domain events that leave the workflow instance through a stream — today the
/// custom-task requests to plugin handlers, or to the external-task queue for the task types
/// <c>Fleans:ExternalTasks:TaskTypes</c> lists (#979). Script tasks are dispatched straight to the script
/// executor grain and conditions are evaluated in the instance, so neither comes through here.
/// </summary>
[StatelessWorker]
public partial class WorkflowEventsPublisher : Grain, IEventPublisher
{
    private IStreamProvider _streamProvider = null!;
    private readonly ILogger<WorkflowEventsPublisher> _logger;
    private readonly ExternalTaskOptions _externalTasks;
    private readonly IExternalTaskQueue? _externalTaskQueue;

    // Stream-namespace constants live in Fleans.Application.Abstractions
    // (Events/WorkflowEventStreams.cs) so plugin handlers can subscribe without
    // taking a transitive ref on Fleans.Application / Fleans.Domain. The aliases
    // below preserve in-tree call sites that still reference the publisher type.
    public const string StreamProvider = WorkflowEventStreams.StreamProvider;
    public const string StreamNameSpace = WorkflowEventStreams.StreamNameSpace;
    public const string ExecuteCustomTaskStreamNamespace = WorkflowEventStreams.ExecuteCustomTaskStreamNamespace;

    public WorkflowEventsPublisher(
        ILogger<WorkflowEventsPublisher> logger,
        IOptions<ExternalTaskOptions> externalTasks,
        IServiceProvider services)
    {
        _logger = logger;
        _externalTasks = externalTasks.Value;
        _externalTaskQueue = services.GetService<IExternalTaskQueue>();
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
            case ExecuteCustomTaskEvent externalTask when _externalTasks.IsExternal(externalTask.TaskType):
                await PublishExternalTask(externalTask);
                break;
            case ExecuteCustomTaskEvent executeCustomTaskEvent:

                // Partition the namespace by TaskType so each plugin's subscriber grain class
                // only sees its own traffic (eliminates the N×M filter-after-deliver fanout), and
                // key the stream by ActivityInstanceId: the key is also the handler grain's key, so
                // each task gets its own activation and parallel branches of one instance run
                // concurrently (#815). See docs/conventions/streaming.md "Custom-task per-type stream namespace".
                var customTaskNs = WorkflowEventStreams.GetExecuteCustomTaskNamespace(executeCustomTaskEvent.TaskType);
                var customTaskStreamId = StreamId.Create(customTaskNs, executeCustomTaskEvent.ActivityInstanceId.ToString("D"));
                var customTaskStream = _streamProvider.GetStream<ExecuteCustomTaskEvent>(customTaskStreamId);
                await customTaskStream.OnNextAsync(executeCustomTaskEvent);
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
        var queue = _externalTaskQueue ?? throw new InvalidOperationException(
            $"Task type '{task.TaskType}' is in Fleans:ExternalTasks:TaskTypes but no external-task queue is " +
            "registered on this silo. Set Fleans:ExternalTasks:Provider.");

        await queue.Publish(task, CancellationToken.None);
        LogExternalTaskPublished(task.ActivityId, task.ActivityInstanceId, task.TaskType);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Debug, Message = "Publishing {EventType}")]
    private partial void LogPublishing(string eventType);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "WorkflowEventsPublisher received unhandled event type {EventTypeName}; dropping. Add a case to Publish if intentional.")]
    private partial void LogUnknownEventType(string eventTypeName);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug,
        Message = "External task {ActivityId} ({ActivityInstanceId}, type {TaskType}) published to the external-task queue")]
    private partial void LogExternalTaskPublished(string activityId, Guid activityInstanceId, string taskType);
}
