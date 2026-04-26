using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates.Services;

public record MultiInstanceTryCompleteResult(
    bool HostCompleted,
    string? HostActivityId);

public record MultiInstanceFailResult(
    Guid HostInstanceId,
    string HostActivityId);

public class MultiInstanceCoordinator
{
    private readonly WorkflowInstanceState _state;
    private readonly Action<IDomainEvent> _emit;

    public MultiInstanceCoordinator(
        WorkflowInstanceState state,
        Action<IDomainEvent> emit)
    {
        _state = state;
        _emit = emit;
    }

    /// <summary>
    /// Checks whether a multi-instance host can complete, spawning the next sequential
    /// iteration or completing the host with aggregated output. Returns whether the host
    /// completed and the host's activity ID for building boundary unsubscribe effects.
    /// </summary>
    public MultiInstanceTryCompleteResult TryComplete(
        ActivityInstanceEntry hostEntry,
        MultiInstanceActivity mi,
        List<ActivityInstanceEntry> scopeEntries)
    {
        var completedIterations = scopeEntries.Where(e => e.IsCompleted).ToList();
        var activeIterations = scopeEntries.Where(e => !e.IsCompleted).ToList();
        var total = hostEntry.MultiInstanceTotal;

        // Host not yet executed (no total set)
        if (total is null)
            return new(false, null);

        // If there are active iterations still running, wait
        if (activeIterations.Count > 0)
            return new(false, null);

        // All spawned iterations are done but fewer than total — sequential: spawn next
        if (completedIterations.Count < total)
        {
            SpawnNextSequentialIteration(hostEntry, mi, completedIterations.Count);
            return new(false, null); // host not completed yet
        }

        // All iterations done — aggregate output variables
        AggregateOutputVariables(hostEntry, mi, scopeEntries);

        // Clean up child variable scopes
        CleanupChildVariableScopes(scopeEntries);

        // Complete the host entry
        _emit(new ActivityCompleted(
            hostEntry.ActivityInstanceId, hostEntry.VariablesId, new ExpandoObject()));

        return new(true, mi.ActivityId);
    }

    /// <summary>
    /// Fails the multi-instance host: cleans up child variable scopes and emits
    /// the host failure event. Returns the host info so the aggregate can cancel
    /// scope children and build cleanup effects.
    /// </summary>
    public MultiInstanceFailResult FailHost(
        ActivityInstanceEntry failedIteration, string errorCode, string errorMessage)
    {
        var hostInstanceId = failedIteration.ScopeId!.Value;
        var hostEntry = _state.GetEntry(hostInstanceId);

        // Clean up child variable scopes
        var scopeEntries = _state.GetEntriesInScope(hostInstanceId)
            .Where(e => e.MultiInstanceIndex.HasValue)
            .ToList();
        var childVarIds = scopeEntries.Select(e => e.VariablesId).ToList();
        if (childVarIds.Count > 0)
            _emit(new VariableScopesRemoved(childVarIds));

        // Fail the host entry
        _emit(new ActivityFailed(hostInstanceId, errorCode, errorMessage));

        return new(hostInstanceId, hostEntry.ActivityId);
    }

    private void SpawnNextSequentialIteration(
        ActivityInstanceEntry hostEntry, MultiInstanceActivity mi, int nextIndex)
    {
        var parentVariablesId = hostEntry.VariablesId;

        // Resolve collection item for next iteration
        object? iterationItem = null;
        if (mi.InputCollection is not null && mi.InputDataItem is not null)
        {
            var collectionVar = _state.GetVariable(parentVariablesId, mi.InputCollection);
            if (collectionVar is IList<object> list)
                iterationItem = list[nextIndex];
            else if (collectionVar is System.Collections.IEnumerable enumerable and not string)
                iterationItem = enumerable.Cast<object>().ElementAt(nextIndex);
        }

        // Create child variable scope for the new iteration
        var childScopeId = Guid.NewGuid();
        _emit(new ChildVariableScopeCreated(childScopeId, parentVariablesId));

        var iterVars = new ExpandoObject();
        var iterDict = (IDictionary<string, object?>)iterVars;
        iterDict["loopCounter"] = nextIndex;
        if (mi.InputDataItem is not null && iterationItem is not null)
            iterDict[mi.InputDataItem] = iterationItem;

        _emit(new VariablesMerged(childScopeId, iterVars));

        // Spawn the next iteration entry
        _emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: mi.ActivityId,
            ActivityType: mi.InnerActivity.GetType().Name,
            VariablesId: childScopeId,
            ScopeId: hostEntry.ActivityInstanceId,
            MultiInstanceIndex: nextIndex,
            TokenId: null));
    }

    private void AggregateOutputVariables(
        ActivityInstanceEntry hostEntry, MultiInstanceActivity mi,
        List<ActivityInstanceEntry> scopeEntries)
    {
        if (mi.OutputDataItem is null || mi.OutputCollection is null)
            return;

        var iterationEntries = scopeEntries
            .Where(e => e.MultiInstanceIndex.HasValue)
            .OrderBy(e => e.MultiInstanceIndex!.Value)
            .ToList();

        var outputList = new List<object?>();
        foreach (var iterEntry in iterationEntries)
        {
            var outputValue = _state.GetVariable(iterEntry.VariablesId, mi.OutputDataItem);
            outputList.Add(outputValue);
        }

        var outputVars = new ExpandoObject();
        ((IDictionary<string, object?>)outputVars)[mi.OutputCollection] = outputList;
        _emit(new VariablesMerged(hostEntry.VariablesId, outputVars));
    }

    private void CleanupChildVariableScopes(List<ActivityInstanceEntry> scopeEntries)
    {
        var childVarIds = scopeEntries
            .Where(e => e.MultiInstanceIndex.HasValue)
            .Select(e => e.VariablesId)
            .ToList();
        if (childVarIds.Count > 0)
        {
            _emit(new VariableScopesRemoved(childVarIds));
        }
    }
}
