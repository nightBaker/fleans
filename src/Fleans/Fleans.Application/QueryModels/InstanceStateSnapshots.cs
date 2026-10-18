using Fleans.Domain;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Application.QueryModels;

/// <summary>
/// Builds the <see cref="InstanceStateSnapshot"/> of one instance. Shared by the two places that
/// serve it: the grain (a running instance) and the read model (a completed one).
/// </summary>
public static class InstanceStateSnapshots
{
    /// <param name="definition">Resolves condition text and endpoints; without it they are empty.</param>
    public static InstanceStateSnapshot From(WorkflowInstanceState state, IWorkflowDefinition? definition)
    {
        var activeEntries = state.Entries.Where(e => !e.IsCompleted).ToList();
        var completedEntries = state.Entries.Where(e => e.IsCompleted).ToList();

        var variableStates = state.VariableStates.Select(vs =>
        {
            var dict = ((IDictionary<string, object?>)vs.Variables)
                .ToDictionary(e => e.Key, e => VariableValueFormatter.Format(e.Value));
            return new VariableStateSnapshot(vs.Id, dict);
        }).ToList();

        return new InstanceStateSnapshot(
            activeEntries.Select(e => e.ActivityId).ToList(),
            completedEntries.Select(e => e.ActivityId).ToList(),
            state.IsStarted, state.IsCompleted, state.IsCancelled, state.IsFailed,
            activeEntries.Select(ToActivitySnapshot).ToList(),
            completedEntries.Select(ToActivitySnapshot).ToList(),
            variableStates,
            state.ConditionSequenceStates.Select(cs => ToConditionSnapshot(cs, definition)).ToList(),
            state.ProcessDefinitionId,
            state.CreatedAt, state.ExecutionStartedAt, state.CompletedAt,
            state.BusinessId);
    }

    private static ActivityInstanceSnapshot ToActivitySnapshot(ActivityInstanceEntry entry)
    {
        return new ActivityInstanceSnapshot(
            entry.ActivityInstanceId, entry.ActivityId, entry.ActivityType ?? "",
            entry.IsCompleted, entry.IsExecuting, entry.IsCancelled, entry.VariablesId,
            entry.ErrorState, entry.CancellationReason,
            entry.CreatedAt, entry.ExecutionStartedAt, entry.CompletedAt,
            entry.ChildWorkflowInstanceId);
    }

    private static ConditionSequenceSnapshot ToConditionSnapshot(
        ConditionSequenceState cs,
        IWorkflowDefinition? definition)
    {
        var flow = definition?.SequenceFlows
            .OfType<ConditionalSequenceFlow>()
            .FirstOrDefault(sf => sf.SequenceFlowId == cs.ConditionalSequenceFlowId);

        return new ConditionSequenceSnapshot(
            cs.ConditionalSequenceFlowId,
            flow?.Condition ?? "",
            flow?.Source.ActivityId ?? "",
            flow?.Target.ActivityId ?? "",
            cs.Result);
    }
}
