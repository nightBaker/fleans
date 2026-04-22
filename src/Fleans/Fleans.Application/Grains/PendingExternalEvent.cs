using System.Dynamic;
using Fleans.Domain;

namespace Fleans.Application.Grains;

/// <summary>
/// Base type for events enqueued by [AlwaysInterleave] methods
/// and processed later by serialized regular methods.
/// These records are in-memory queue items only — they never cross grain boundaries,
/// so [GenerateSerializer] is intentionally omitted.
/// </summary>
public abstract record PendingExternalEvent;

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
    TaskCompletionSource<EscalationHandledResult> Tcs) : PendingExternalEvent;
