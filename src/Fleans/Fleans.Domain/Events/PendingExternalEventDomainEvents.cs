using System.Dynamic;
using Fleans.Domain;

namespace Fleans.Domain.Events;

// ── Pending-external-event durability (issue #657) ──────────────────────────
//
// WorkflowInstance maintains an in-memory queue of external events (child
// completed/failed/escalation, signal delivery) drained on a grain timer. On a
// forced deactivation the queue — and the TaskCompletionSources awaiting drain —
// are lost. These events persist that queue via the JournaledGrain event log so
// it survives reactivation, and add an op-id ledger so caller-retried operations
// are not double-applied.
//
// The payload hierarchy mirrors the in-memory PendingExternalEvent records but
// drops the (non-serializable) TaskCompletionSource. Fresh TCSes are minted on
// reactivation; retried callers short-circuit via the AppliedOperations ledger.

/// <summary>An external event was enqueued for later draining. Persisted so the queue survives deactivation.</summary>
[GenerateSerializer]
public record PendingEventEnqueued(
    [property: Id(0)] string OpKey,
    [property: Id(1)] PendingExternalEventPayload Payload,
    [property: Id(2)] DateTimeOffset OccurredAt = default) : IDomainEvent;

/// <summary>A previously-enqueued external event was drained (processed + applied). Removes it from PendingOperations.</summary>
[GenerateSerializer]
public record PendingEventDrained(
    [property: Id(0)] string OpKey,
    [property: Id(1)] DateTimeOffset OccurredAt = default) : IDomainEvent;

/// <summary>
/// Records the durable outcome of a caller-retried operation in the dedup ledger
/// (AppliedOperations). Result is non-null only for value-returning ops (escalation).
/// </summary>
[GenerateSerializer]
public record PendingEventApplied(
    [property: Id(0)] string OpKey,
    [property: Id(1)] PendingEventResult? Result,
    [property: Id(2)] DateTimeOffset OccurredAt = default) : IDomainEvent;

// ── Serializable payload hierarchy (mirrors PendingExternalEvent, minus the TCS) ──

[GenerateSerializer]
public abstract record PendingExternalEventPayload;

[GenerateSerializer]
public sealed record PendingChildCompletedPayload(
    [property: Id(0)] string ParentActivityId,
    [property: Id(1)] ExpandoObject ChildVariables) : PendingExternalEventPayload;

[GenerateSerializer]
public sealed record PendingChildFailedPayload(
    [property: Id(0)] string ParentActivityId,
    [property: Id(1)] string ExceptionMessage) : PendingExternalEventPayload;

[GenerateSerializer]
public sealed record PendingSignalDeliveryPayload(
    [property: Id(0)] string ActivityId,
    [property: Id(1)] Guid HostActivityInstanceId) : PendingExternalEventPayload;

[GenerateSerializer]
public sealed record PendingBoundarySignalFiredPayload(
    [property: Id(0)] string BoundaryActivityId,
    [property: Id(1)] Guid HostActivityInstanceId) : PendingExternalEventPayload;

[GenerateSerializer]
public sealed record PendingChildEscalationRaisedPayload(
    [property: Id(0)] Guid ChildWorkflowInstanceId,
    [property: Id(1)] string HostActivityId,
    [property: Id(2)] string EscalationCode,
    [property: Id(3)] ExpandoObject Variables,
    [property: Id(4)] Guid EscalationInstanceId) : PendingExternalEventPayload;

/// <summary>
/// Serializable wrapper for the durable outcome of a value-returning pending op.
/// Currently only the escalation path returns a value.
/// </summary>
[GenerateSerializer]
public sealed record PendingEventResult(
    [property: Id(0)] EscalationHandledResult EscalationResult);
