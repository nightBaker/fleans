namespace Fleans.Persistence;

/// <summary>
/// Flags enum matching the dirty-flag int constants on
/// <see cref="Domain.States.WorkflowInstanceState"/>. Used by
/// <see cref="EfCoreWorkflowStateProjection"/> to interpret the dirty flags
/// and conditionally skip Include/Diff for unchanged collections.
/// </summary>
[Flags]
internal enum DirtyCollections
{
    None = 0,
    Entries = 1,
    VariableStates = 2,
    ConditionSequenceStates = 4,
    GatewayForks = 8,
    TimerCycleTracking = 16
}
