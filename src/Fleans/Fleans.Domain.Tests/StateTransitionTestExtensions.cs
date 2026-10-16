using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// Clock-stamping overloads for tests that drive state directly. Production state transitions
/// take their time from the journaled event (<c>WorkflowEvent.OccurredAt</c>) so replay is
/// deterministic; tests that bypass events use the wall clock instead.
/// </summary>
internal static class StateTransitionTestExtensions
{
    public static void Execute(this ActivityInstanceEntry entry) => entry.Execute(DateTimeOffset.UtcNow);
    public static void Complete(this ActivityInstanceEntry entry) => entry.Complete(DateTimeOffset.UtcNow);
    public static void Fail(this ActivityInstanceEntry entry, Exception exception) => entry.Fail(exception, DateTimeOffset.UtcNow);
    public static void Fail(this ActivityInstanceEntry entry, string errorCode, string errorMessage) => entry.Fail(errorCode, errorMessage, DateTimeOffset.UtcNow);
    public static void Cancel(this ActivityInstanceEntry entry, string reason) => entry.Cancel(reason, DateTimeOffset.UtcNow);

    public static void StartWith(this WorkflowInstanceState state, Guid id, string? processDefinitionId, ActivityInstanceEntry entry, Guid variablesId)
        => state.StartWith(id, processDefinitionId, entry, variablesId, DateTimeOffset.UtcNow);
    public static void Start(this WorkflowInstanceState state) => state.Start(DateTimeOffset.UtcNow);
    public static void Complete(this WorkflowInstanceState state) => state.Complete(DateTimeOffset.UtcNow);
    public static void Cancel(this WorkflowInstanceState state) => state.Cancel(DateTimeOffset.UtcNow);
    public static void Fail(this WorkflowInstanceState state) => state.Fail(DateTimeOffset.UtcNow);
    public static void CompleteEntries(this WorkflowInstanceState state, List<ActivityInstanceEntry> entries) => state.CompleteEntries(entries, DateTimeOffset.UtcNow);
    public static void CompleteEntry(this WorkflowInstanceState state, Guid activityInstanceId) => state.CompleteEntry(activityInstanceId, DateTimeOffset.UtcNow);
    public static void FailEntry(this WorkflowInstanceState state, Guid activityInstanceId, string errorCode, string errorMessage)
        => state.FailEntry(activityInstanceId, errorCode, errorMessage, DateTimeOffset.UtcNow);
}
