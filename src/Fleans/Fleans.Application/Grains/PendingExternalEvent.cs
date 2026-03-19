using System.Dynamic;

namespace Fleans.Application.Grains;

/// <summary>
/// Base type for events enqueued by [AlwaysInterleave] methods
/// and processed later by serialized regular methods.
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
