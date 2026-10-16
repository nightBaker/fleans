using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// Conveniences for tests that drive state directly, bypassing events. Production state
/// transitions take ids and time from the journaled event (<c>WorkflowEvent.OccurredAt</c>) so
/// replay is deterministic; these helpers use the wall clock and fresh Guids instead.
/// </summary>
internal static class StateTransitionTestExtensions
{
    public static void Execute(this ActivityInstanceEntry entry) => entry.Execute(DateTimeOffset.UtcNow);
    public static void Complete(this ActivityInstanceEntry entry) => entry.Complete(DateTimeOffset.UtcNow);
    public static void Fail(this ActivityInstanceEntry entry, string errorCode, string errorMessage) => entry.Fail(errorCode, errorMessage, DateTimeOffset.UtcNow);
    public static void Cancel(this ActivityInstanceEntry entry, string reason) => entry.Cancel(reason, DateTimeOffset.UtcNow);

    public static void StartWith(this WorkflowInstanceState state, Guid id, string? processDefinitionId, ActivityInstanceEntry entry, Guid variablesId)
    {
        state.Initialize(id, processDefinitionId, variablesId, DateTimeOffset.UtcNow);
        state.AddEntries([entry]);
    }
    public static void Start(this WorkflowInstanceState state) => state.Start(DateTimeOffset.UtcNow);
    public static void Complete(this WorkflowInstanceState state) => state.Complete(DateTimeOffset.UtcNow);
    public static void Cancel(this WorkflowInstanceState state) => state.Cancel(DateTimeOffset.UtcNow);
    public static void Fail(this WorkflowInstanceState state) => state.Fail(DateTimeOffset.UtcNow);
    public static void CompleteEntries(this WorkflowInstanceState state, List<ActivityInstanceEntry> entries)
    {
        foreach (var entry in entries)
            state.CompleteEntry(entry.ActivityInstanceId, DateTimeOffset.UtcNow);
    }
    public static void CompleteEntry(this WorkflowInstanceState state, Guid activityInstanceId) => state.CompleteEntry(activityInstanceId, DateTimeOffset.UtcNow);
    public static Guid AddChildVariableState(this WorkflowInstanceState state, Guid parentVariablesId)
    {
        var id = Guid.NewGuid();
        state.AddChildVariableState(id, parentVariablesId);
        return id;
    }

    public static Guid AddCloneOfVariableState(this WorkflowInstanceState state, Guid sourceScopeId)
    {
        var id = Guid.NewGuid();
        state.AddCloneOfVariableState(id, sourceScopeId);
        return id;
    }

    public static void FailEntry(this WorkflowInstanceState state, Guid activityInstanceId, string errorCode, string errorMessage)
        => state.FailEntry(activityInstanceId, errorCode, errorMessage, DateTimeOffset.UtcNow);
}
