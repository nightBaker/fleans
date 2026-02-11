using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Domain.States;

// TODO: Add [GenerateSerializer] and [Id] attributes before implementing real storage.
// Fields must become public properties for Orleans serialization.
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

    public bool IsStarted() => _isStarted;
    public bool IsCompleted() => _isCompleted;

    public IReadOnlyList<IActivityInstance> GetCompletedActivities()
        => _completedActivities;

    public IReadOnlyDictionary<Guid, WorklfowVariablesState> GetVariableStates()
        => _variableStates;

    public IReadOnlyDictionary<Guid, ConditionSequenceState[]> GetConditionSequenceStates()
        => _conditionSequenceStates;

    public IReadOnlyList<IActivityInstance> GetActiveActivities()
        => _activeActivities;

    public void StartWith(IActivityInstance activityInstance, Guid variablesId)
    {
        _variableStates.Add(variablesId, new WorklfowVariablesState());
        _activeActivities.Add(activityInstance);
    }

    public void Start()
    {
        if (_isStarted)
            throw new InvalidOperationException("Workflow is already started");

        _isStarted = true;
    }

    public void Complete()
    {
        if (_isCompleted)
            throw new InvalidOperationException("Workflow is already completed");

        _isCompleted = true;
    }

    public Guid AddCloneOfVariableState(Guid variableStateId)
    {
        var newVariableStateId = Guid.NewGuid();

        var clonedState = new WorklfowVariablesState();
        clonedState.CloneFrom(_variableStates[variableStateId]);

        _variableStates.Add(newVariableStateId, clonedState);
        return newVariableStateId;
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences)
    {
        var sequenceStates = sequences.Select(sequence => new ConditionSequenceState(sequence)).ToArray();
        _conditionSequenceStates.Add(activityInstanceId, sequenceStates);
    }

    public void RemoveActiveActivities(List<IActivityInstance> removeInstances)
    {
        _activeActivities.RemoveAll(removeInstances.Contains);
    }

    public void AddActiveActivities(IEnumerable<IActivityInstance> activities)
    {
        _activeActivities.AddRange(activities);
    }

    public void AddCompletedActivities(IEnumerable<IActivityInstance> activities)
    {
        _completedActivities.AddRange(activities);
    }

    public async Task<IActivityInstance?> GetFirstActive(string activityId)
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

    public async Task<bool> AnyNotExecuting()
    {
        foreach(var activity in _activeActivities)
        {
            if (!await activity.IsExecuting())
                return true;
        }

        return false;
    }

    public async Task<IActivityInstance[]> GetNotExecutingNotCompletedActivities()
    {
        var result = new List<IActivityInstance>();

        foreach(var activity in _activeActivities)
        {
            if (!await activity.IsExecuting() && !await activity.IsCompleted())
                result.Add(activity);
        }

        return result.ToArray();
    }

    public void SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
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
    }

    public void MergeState(Guid variablesId, ExpandoObject variables)
    {
        _variableStates[variablesId].Merge(variables);
    }

    public async Task<InstanceStateSnapshot> GetStateSnapshot()
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
