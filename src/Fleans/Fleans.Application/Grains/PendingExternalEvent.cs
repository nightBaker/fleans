using System.Dynamic;
using Fleans.Domain;

namespace Fleans.Application.Grains;

/// <summary>
/// Base type for events enqueued by [AlwaysInterleave] methods
/// and processed later by serialized regular methods.
/// These records are in-memory queue items only — they never cross grain boundaries,
/// so [GenerateSerializer] is intentionally omitted.
/// </summary>
public abstract record PendingExternalEvent
{
    /// <summary>
    /// Op-key identifying this enqueued event in the durable PendingOperations ledger.
    /// Used by the drain path to emit PendingEventDrained/PendingEventApplied (#657).
    /// </summary>
    public string OperationId { get; init; } = "";
}

public record PendingChildCompleted(
    string ParentActivityId,
    ExpandoObject ChildVariables) : PendingExternalEvent;

public record PendingChildFailed(
    string ParentActivityId,
    Exception Exception) : PendingExternalEvent;

public record PendingSignalDelivery(
    string ActivityId,
    Guid HostActivityInstanceId) : PendingExternalEvent;

public record PendingBoundarySignalFired(
    string BoundaryActivityId,
    Guid HostActivityInstanceId) : PendingExternalEvent;

public record PendingChildEscalationRaised(
    Guid ChildWorkflowInstanceId,
    string HostActivityId,
    string EscalationCode,
    ExpandoObject Variables,
    TaskCompletionSource<EscalationHandledResult> Tcs) : PendingExternalEvent
{
    /// <summary>Origin throw's activity-instance id, threaded so the re-escalation hop keeps the same op-id (#657).</summary>
    public Guid EscalationInstanceId { get; init; }
}
