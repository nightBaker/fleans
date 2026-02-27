using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Fleans.Application.Services;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    public async Task StartWorkflow()
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowStarted();
        State.Start();
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }

    private async Task ExecuteWorkflow()
    {
        var definition = await GetWorkflowDefinition();
        while (await AnyNotExecuting())
        {
            foreach (var activityState in await GetNotExecutingNotCompletedActivities())
            {
                var activityId = await activityState.GetActivityId();
                var scopeDefinition = definition.GetScopeForActivity(activityId);
                var currentActivity = scopeDefinition.GetActivity(activityId);
                SetActivityRequestContext(activityId, activityState);
                LogExecutingActivity(activityId, currentActivity.GetType().Name);
                var commands = await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
                var currentEntry = State.GetActiveEntry(activityState.GetPrimaryKey());
                await ProcessCommands(commands, currentEntry, activityState);
            }

            await TransitionToNextActivity();
            LogStatePersistedAfterTransition();
            await _state.WriteStateAsync();
        }
    }

    private async Task ProcessCommands(
        IReadOnlyList<IExecutionCommand> commands,
        ActivityInstanceEntry entry,
        IActivityExecutionContext activityContext)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case SpawnActivityCommand spawn:
                    await SpawnActivity(spawn, activityContext);
                    break;

                case OpenSubProcessCommand sub:
                    await OpenSubProcessScope(entry.ActivityInstanceId, sub.SubProcess, sub.ParentVariablesId);
                    break;

                case RegisterTimerCommand timer:
                    await RegisterTimerReminder(entry.ActivityInstanceId, timer.TimerActivityId, timer.DueTime);
                    break;

                case RegisterMessageCommand msg:
                    await HandleRegisterMessage(msg, entry.ActivityInstanceId);
                    break;

                case RegisterSignalCommand sig:
                    await HandleRegisterSignal(sig, entry.ActivityInstanceId);
                    break;

                case StartChildWorkflowCommand child:
                    await StartChildWorkflow(child.CallActivity, activityContext);
                    break;

                case AddConditionsCommand cond:
                    await HandleAddConditions(cond, entry, activityContext);
                    break;

                case ThrowSignalCommand sig:
                    await ThrowSignal(sig.SignalName);
                    break;

                case CompleteWorkflowCommand:
                    await Complete();
                    break;
            }
        }
    }

    private async Task SpawnActivity(SpawnActivityCommand spawn, IActivityExecutionContext activityContext)
    {
        var spawnId = Guid.NewGuid();
        var spawnInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(spawnId);
        await spawnInstance.SetActivity(spawn.Activity.ActivityId, spawn.Activity.GetType().Name);
        var spawnVarsId = await activityContext.GetVariablesStateId();
        await spawnInstance.SetVariablesId(spawnVarsId);
        var spawnEntry = new ActivityInstanceEntry(spawnId, spawn.Activity.ActivityId, State.Id, spawn.ScopeId);
        State.AddEntries([spawnEntry]);
    }

    private async Task HandleRegisterMessage(RegisterMessageCommand msg, Guid activityInstanceId)
    {
        if (msg.IsBoundary)
            await RegisterBoundaryMessageSubscription(msg.VariablesId,
                activityInstanceId, msg.ActivityId, msg.MessageDefinitionId);
        else
            await RegisterMessageSubscription(msg.VariablesId, msg.MessageDefinitionId, msg.ActivityId);
    }

    private async Task HandleRegisterSignal(RegisterSignalCommand sig, Guid activityInstanceId)
    {
        if (sig.IsBoundary)
            await RegisterBoundarySignalSubscription(activityInstanceId, sig.ActivityId, sig.SignalName);
        else
            await RegisterSignalSubscription(sig.SignalName, sig.ActivityId, activityInstanceId);
    }

    private async Task HandleAddConditions(AddConditionsCommand cond, ActivityInstanceEntry entry, IActivityExecutionContext activityContext)
    {
        await AddConditionSequenceStates(entry.ActivityInstanceId, cond.SequenceFlowIds);
        var definition = await GetWorkflowDefinition();
        var instanceId = await GetWorkflowInstanceId();
        foreach (var eval in cond.Evaluations)
        {
            await activityContext.PublishEvent(new Domain.Events.EvaluateConditionEvent(
                instanceId,
                definition.WorkflowId,
                definition.ProcessDefinitionId,
                entry.ActivityInstanceId,
                entry.ActivityId,
                eval.SequenceFlowId,
                eval.Condition));
        }
    }

    private async Task OpenSubProcessScope(Guid subProcessInstanceId, SubProcess subProcess, Guid parentVariablesId)
    {
        var childVariablesId = State.AddChildVariableState(parentVariablesId);
        LogSubProcessInitialized(subProcess.ActivityId, childVariablesId);

        var startActivity = subProcess.Activities.FirstOrDefault(a => a is StartEvent)
            ?? throw new InvalidOperationException($"SubProcess '{subProcess.ActivityId}' must have a StartEvent");

        var startInstanceId = Guid.NewGuid();
        var startInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(startInstanceId);
        await startInstance.SetActivity(startActivity.ActivityId, startActivity.GetType().Name);
        await startInstance.SetVariablesId(childVariablesId);

        var startEntry = new ActivityInstanceEntry(startInstanceId, startActivity.ActivityId, State.Id, subProcessInstanceId);
        State.AddEntries([startEntry]);
    }

    private async Task<ActivityInstanceEntry> CreateNextActivityEntry(
        Activity sourceActivity, IActivityInstanceGrain sourceInstance,
        Activity nextActivity, Guid? scopeId)
    {
        var sourceVariablesId = await sourceInstance.GetVariablesStateId();
        var variablesId = sourceActivity is ParallelGateway { IsFork: true }
            ? State.AddCloneOfVariableState(sourceVariablesId)
            : sourceVariablesId;
        RequestContext.Set("VariablesId", variablesId.ToString());

        var newId = Guid.NewGuid();
        var newInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(newId);
        await newInstance.SetVariablesId(variablesId);
        await newInstance.SetActivity(nextActivity.ActivityId, nextActivity.GetType().Name);

        return new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id, scopeId);
    }

    private async Task TransitionToNextActivity()
    {
        var definition = await GetWorkflowDefinition();
        var newActiveEntries = new List<ActivityInstanceEntry>();
        var completedEntries = new List<ActivityInstanceEntry>();

        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);

            if (await activityInstance.IsCompleted())
            {
                completedEntries.Add(entry);

                // Failed or cancelled activities don't transition to next activities
                if (await activityInstance.GetErrorState() is not null)
                    continue;

                if (await activityInstance.IsCancelled())
                    continue;

                var scopeDefinition = definition.GetScopeForActivity(entry.ActivityId);
                var currentActivity = scopeDefinition.GetActivity(entry.ActivityId);

                var nextActivities = await currentActivity.GetNextActivities(this, activityInstance, scopeDefinition);

                foreach(var nextActivity in nextActivities)
                {
                    // For join gateways, reuse the existing active entry instead of creating a duplicate
                    if (nextActivity.IsJoinGateway)
                    {
                        var existingEntry = State.GetActiveActivities()
                            .FirstOrDefault(e => e.ActivityId == nextActivity.ActivityId && e.ScopeId == entry.ScopeId);
                        if (existingEntry != null)
                        {
                            LogJoinGatewayDeduplication(nextActivity.ActivityId, existingEntry.ActivityInstanceId);
                            var existingInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(existingEntry.ActivityInstanceId);
                            await existingInstance.ResetExecuting();
                            continue;
                        }
                    }

                    var newEntry = await CreateNextActivityEntry(currentActivity, activityInstance, nextActivity, entry.ScopeId);
                    newActiveEntries.Add(newEntry);
                }
            }
        }

        LogTransition(completedEntries.Count, newActiveEntries.Count);
        LogStateCompleteEntries(completedEntries.Count);
        State.CompleteEntries(completedEntries);
        LogStateAddEntries(newActiveEntries.Count);
        State.AddEntries(newActiveEntries);

        await CompleteFinishedSubProcessScopes(definition);
    }

    private async Task CompleteFinishedSubProcessScopes(IWorkflowDefinition definition)
    {
        const int maxIterations = 100;
        var iteration = 0;
        bool anyCompleted;
        do
        {
            if (++iteration > maxIterations)
                throw new InvalidOperationException("Sub-process completion loop exceeded max iterations — possible cycle in scope graph");

            anyCompleted = false;
            foreach (var entry in State.GetActiveActivities().ToList())
            {
                var scopeDefinition = definition.GetScopeForActivity(entry.ActivityId);
                var activity = scopeDefinition.GetActivity(entry.ActivityId);
                if (activity is not SubProcess) continue;

                var scopeEntries = State.Entries.Where(e => e.ScopeId == entry.ActivityInstanceId).ToList();
                if (scopeEntries.Count == 0) continue;
                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // All scope children are done — complete the sub-process
                var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
                await activityInstance.Complete();
                LogSubProcessCompleted(entry.ActivityId);

                var nextActivities = await activity.GetNextActivities(this, activityInstance, scopeDefinition);

                var completedEntries = new List<ActivityInstanceEntry> { entry };
                var newEntries = new List<ActivityInstanceEntry>();

                foreach (var nextActivity in nextActivities)
                {
                    var newEntry = await CreateNextActivityEntry(activity, activityInstance, nextActivity, entry.ScopeId);
                    newEntries.Add(newEntry);
                }

                State.CompleteEntries(completedEntries);
                State.AddEntries(newEntries);
                anyCompleted = true;
            }
        } while (anyCompleted);
    }

    public async Task CancelScopeChildren(Guid scopeId)
    {
        var cancelledEntries = new List<ActivityInstanceEntry>();
        foreach (var entry in State.GetActiveActivities().Where(e => e.ScopeId == scopeId).ToList())
        {
            // Recursively cancel nested sub-process children
            if (State.Entries.Any(e => e.ScopeId == entry.ActivityInstanceId && !e.IsCompleted))
            {
                await CancelScopeChildren(entry.ActivityInstanceId);
            }

            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            await activityInstance.Cancel("Sub-process scope cancelled by boundary event");
            cancelledEntries.Add(entry);
            LogScopeChildCancelled(entry.ActivityId, scopeId);
        }
        State.CompleteEntries(cancelledEntries);
    }

    private async Task<bool> AnyNotExecuting()
    {
        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            if (!await activityInstance.IsExecuting())
                return true;
        }
        return false;
    }

    private async Task<IActivityInstanceGrain[]> GetNotExecutingNotCompletedActivities()
    {
        var result = new List<IActivityInstanceGrain>();
        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            if (!await activityInstance.IsExecuting() && !await activityInstance.IsCompleted())
                result.Add(activityInstance);
        }
        return result.ToArray();
    }
}
