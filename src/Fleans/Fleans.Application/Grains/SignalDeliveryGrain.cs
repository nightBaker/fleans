using Fleans.Application.Abstractions.Events;
using Fleans.Application.Placement;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Grains;

/// <summary>
/// Calls <see cref="IWorkflowInstanceGrain.HandleSignalDelivery"/> for each
/// <see cref="SignalDeliveryRequested"/> a signal bucket published on the instance's stream (#976),
/// so a broadcast to a large bucket does not wait for every instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>At-least-once.</b> A transient failure (timeout, unreachable silo, instance reloading) is
/// rethrown, so the stream redelivers the event within <c>MaxEventDeliveryTime</c>. The bucket
/// fixed the event's op-id when it took the subscription, and the instance dedups on it, so a
/// redelivery after a call that did reach the instance is applied once. Any other failure (the
/// instance rejects the delivery) is logged and dropped, as on the bucket's direct path.
/// </para>
/// <para>
/// <b>Placement.</b> On the core silos, next to the buckets and the instances it calls.
/// </para>
/// </remarks>
[ImplicitStreamSubscription(WorkflowEventStreams.SignalDeliveryStreamNamespace)]
[CorePlacement]
public partial class SignalDeliveryGrain : Grain, ISignalDeliveryGrain, IAsyncObserver<SignalDeliveryRequested>
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalDeliveryGrain> _logger;

    public SignalDeliveryGrain(IGrainFactory grainFactory, ILogger<SignalDeliveryGrain> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        // Stream key matches the grain's primary key — see streaming.md "Subscriber-side stream-id trap".
        var streamId = StreamId.Create(WorkflowEventStreams.SignalDeliveryStreamNamespace, this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<SignalDeliveryRequested>(streamId);

        foreach (var handle in await stream.GetAllSubscriptionHandles())
            await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(SignalDeliveryRequested item, StreamSequenceToken? token = null)
    {
        try
        {
            var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);
            await workflowInstance.HandleSignalDelivery(item.OperationId, item.ActivityId, item.HostActivityInstanceId);
            LogDelivered(item.SignalName, item.WorkflowInstanceId, item.ActivityId);
        }
        catch (Exception ex) when (SignalCorrelationGrain.IsTransient(ex))
        {
            LogDeliveryRetried(item.SignalName, item.WorkflowInstanceId, item.ActivityId, ex);
            throw; // the stream redelivers; the instance dedups on the op-id
        }
        catch (Exception ex)
        {
            LogDeliveryDropped(item.SignalName, item.WorkflowInstanceId, item.ActivityId, ex);
        }
    }

    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 9123, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' delivered from the stream to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDelivered(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9124, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId} failed transiently; the stream redelivers")]
    private partial void LogDeliveryRetried(string signalName, Guid workflowInstanceId, string activityId, Exception exception);

    [LoggerMessage(EventId = 9125, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId} was rejected and dropped")]
    private partial void LogDeliveryDropped(string signalName, Guid workflowInstanceId, string activityId, Exception exception);

    [LoggerMessage(EventId = 9126, Level = LogLevel.Error, Message = "SignalDelivery stream error")]
    private partial void LogStreamError(Exception exception);
}
