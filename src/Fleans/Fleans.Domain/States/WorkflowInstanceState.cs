﻿using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans;
using System.Collections.Generic;

namespace Fleans.Domain.States;

public class WorkflowInstanceState : Grain, IWorkflowInstanceState
{    
    private readonly List<IActivityInstance> _activeActivities = new();    
    private readonly List<IActivityInstance> _completedActivities = new();    
    private readonly Dictionary<Guid, WorklfowVariablesState> _variableStates = new();
    private readonly Dictionary<Guid, ConditionSequenceState[]> _conditionSequenceStates = new();
    private bool _isStarted;
    private bool _isCompleted;

    private readonly IGrainFactory _grainFactory;

    public WorkflowInstanceState(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

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

    public void StartWith(Activity startActivity)
    {
        var variablesId = Guid.NewGuid();
        _variableStates.Add(variablesId, new WorklfowVariablesState());

        var activityInstance = _grainFactory.GetGrain<IActivityInstance>(variablesId);
        activityInstance.SetActivity(startActivity);

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
        if (!_activeActivities.Any())
            throw new InvalidOperationException("Workflow is already completed");

        _isCompleted = true;
    }

    public ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId)
    {
        var newVariableStateId = Guid.NewGuid();

        var clonedState = new WorklfowVariablesState();
        clonedState.CloneFrom(_variableStates[variableStateId]);

        _variableStates.Add(newVariableStateId, clonedState);
        return ValueTask.FromResult(newVariableStateId);
    }

    public void AddConditionSequenceStates(Guid activityInstanceId, IEnumerable<ConditionalSequenceFlow> sequences)
    {
        var sequenceStates = sequences.Select(sequence => new ConditionSequenceState(sequence)).ToArray();
        _conditionSequenceStates.Add(activityInstanceId, sequenceStates);
    }

    public void RemoveActiveActivities(List<IActivityInstance> removeInstances) => _activeActivities.RemoveAll(removeInstances.Contains);

    public void AddActiveActivities(IEnumerable<IActivityInstance> activities) => _activeActivities.AddRange(activities);

    public void AddCompletedActivities(IEnumerable<IActivityInstance> activities) => _completedActivities.AddRange(activities);

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
}
