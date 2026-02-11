using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Domain.States;

public class WorkflowInstanceState
{
    private readonly List<IActivityInstance> _activeActivities = new();
    private readonly List<IActivityInstance> _completedActivities = new();
    private readonly Dictionary<Guid, WorklfowVariablesState> _variableStates = new();
    private readonly Dictionary<Guid, ConditionSequenceState[]> _conditionSequenceStates = new();
    private bool _isStarted;
    private bool _isCompleted;

    public DateTimeOffset? CreatedAt { get; internal set; }
    public DateTimeOffset? ExecutionStartedAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    public ValueTask<bool> IsStarted() => ValueTask.FromResult(_isStarted);
    public ValueTask<bool> IsCompleted() => ValueTask.FromResult(_isCompleted);

    public ValueTask<IReadOnlyList<IActivityInstance>> GetCompletedActivities()
        => ValueTask.FromResult(_completedActivities as IReadOnlyList<IActivityInstance>);

    public ValueTask<IReadOnlyDictionary<Guid, WorklfowVariablesState>> GetVariableStates()
        => ValueTask.FromResult(_variableStates as IReadOnlyDictionary<Guid, WorklfowVariablesState>);

    public ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates()
        => ValueTask.FromResult(_conditionSequenceStates as IReadOnlyDictionary<Guid, ConditionSequenceState[]>);

    public ValueTask<IReadOnlyList<IActivityInstance>> GetActiveActivities()
        => ValueTask.FromResult(_activeActivities as IReadOnlyList<IActivityInstance>);

    public ValueTask StartWith(IActivityInstance activityInstance, Guid variablesId)
    {
        _variableStates.Add(variablesId, new WorklfowVariablesState());
        _activeActivities.Add(activityInstance);
        return ValueTask.CompletedTask;
    }

    public ValueTask Start()
    {
        if (_isStarted)
            throw new InvalidOperationException("Workflow is already started");

        _isStarted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask Complete()
    {
        if (!_activeActivities.Any())
            throw new InvalidOperationException("Workflow is already completed");

        _isCompleted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId)
    {
        var newVariableStateId = Guid.NewGuid();

        var clonedState = new WorklfowVariablesState();
        clonedState.CloneFrom(_variableStates[variableStateId]);

        _variableStates.Add(newVariableStateId, clonedState);
        return ValueTask.FromResult(newVariableStateId);
    }

    public ValueTask AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences)
    {
        var sequenceStates = sequences.Select(sequence => new ConditionSequenceState(sequence)).ToArray();
        _conditionSequenceStates.Add(activityInstanceId, sequenceStates);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveActiveActivities(List<IActivityInstance> removeInstances)
    {
        _activeActivities.RemoveAll(removeInstances.Contains);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddActiveActivities(IEnumerable<IActivityInstance> activities)
    {
        _activeActivities.AddRange(activities);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddCompletedActivities(IEnumerable<IActivityInstance> activities)
    {
        _completedActivities.AddRange(activities);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IActivityInstance?> GetFirstActive(string activityId)
    {
        foreach (var activeActivity in _activeActivities)
        {
            var currentActivity = await activeActivity.GetCurrentActivity();
            if (currentActivity.ActivityId == activityId)
            {
                return activeActivity;
            }
        }

        return null;
    }

    public async ValueTask<bool> AnyNotExecuting()
    {
        foreach(var activity in _activeActivities)
        {
            if (!await activity.IsExecuting())
                return true;
        }

        return false;
    }

    public async ValueTask<IActivityInstance[]> GetNotExecutingNotCompletedActivities()
    {
        var result = new List<IActivityInstance>();

        foreach(var activity in _activeActivities)
        {
            if (!await activity.IsExecuting() && !await activity.IsCompleted())
                result.Add(activity);
        }

        return result.ToArray();
    }

    public ValueTask SetCondigitionSequencesResult(Guid activityInstanceId, string sequenceId, bool result)
    {
        var sequences = _conditionSequenceStates[activityInstanceId];

        var sequence = sequences.FirstOrDefault(s => s.ConditionalSequence.SequenceFlowId == sequenceId);

        if (sequence != null)
        {
            sequence.SetResult(result);
        }
        else
        {
            throw new NullReferenceException("Sequence not found");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MergeState(Guid variablesId, ExpandoObject variables)
    {
        _variableStates[variablesId].Merge(variables);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<InstanceStateSnapshot> GetStateSnapshot()
    {
        var activeTasks = _activeActivities.Select(a => a.GetSnapshot().AsTask());
        var completedTasks = _completedActivities.Select(a => a.GetSnapshot().AsTask());
        var allTasks = activeTasks.Concat(completedTasks).ToList();
        var allSnapshots = await Task.WhenAll(allTasks);

        var activeSnapshots = allSnapshots.Take(_activeActivities.Count).ToList();
        var completedSnapshots = allSnapshots.Skip(_activeActivities.Count).ToList();

        var activeIds = activeSnapshots.Select(s => s.ActivityId).ToList();
        var completedIds = completedSnapshots.Select(s => s.ActivityId).ToList();

        var variableStates = _variableStates.Select(kvp =>
        {
            var dict = ((IDictionary<string, object>)kvp.Value.Variables)
                .ToDictionary(e => e.Key, e => e.Value?.ToString() ?? "");
            return new VariableStateSnapshot(kvp.Key, dict);
        }).ToList();

        var conditionSequences = _conditionSequenceStates
            .SelectMany(kvp => kvp.Value.Select(cs => new ConditionSequenceSnapshot(
                cs.ConditionalSequence.SequenceFlowId,
                cs.ConditionalSequence.Condition,
                cs.ConditionalSequence.Source.ActivityId,
                cs.ConditionalSequence.Target.ActivityId,
                cs.Result)))
            .ToList();

        return new InstanceStateSnapshot(
            activeIds, completedIds, _isStarted, _isCompleted,
            activeSnapshots, completedSnapshots,
            variableStates, conditionSequences);
    }
}
