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

        ActivityInstanceEntry spawnEntry;

        if (spawn.MultiInstanceIndex is not null && spawn.ParentVariablesId is not null)
        {
            // Set total on host entry (idempotent — first spawn sets it)
            if (spawn.MultiInstanceTotal is not null && spawn.HostActivityInstanceId is not null)
            {
                var hostEntry = State.GetActiveEntry(spawn.HostActivityInstanceId.Value);
                if (hostEntry.MultiInstanceTotal is null)
                {
                    hostEntry.SetMultiInstanceTotal(spawn.MultiInstanceTotal.Value);
                    var definition = await GetWorkflowDefinition();
                    var scopeDef = definition.GetScopeForActivity(hostEntry.ActivityId);
                    var miActivity = scopeDef.GetActivity(hostEntry.ActivityId) as MultiInstanceActivity;
                    LogMultiInstanceScopeOpened(hostEntry.ActivityId, spawn.MultiInstanceTotal.Value, miActivity?.IsSequential ?? false);
                }
            }

            // MI iteration: create child variable scope with iteration variables
            var childVariablesId = State.AddChildVariableState(spawn.ParentVariablesId.Value);

            dynamic iterVars = new System.Dynamic.ExpandoObject();
            var iterDict = (IDictionary<string, object?>)iterVars;
            iterDict["loopCounter"] = spawn.MultiInstanceIndex.Value;
            if (spawn.IterationItem is not null && spawn.IterationItemName is not null)
                iterDict[spawn.IterationItemName] = spawn.IterationItem;
            State.MergeState(childVariablesId, (System.Dynamic.ExpandoObject)iterVars);

            await spawnInstance.SetVariablesId(childVariablesId);
            await spawnInstance.SetMultiInstanceIndex(spawn.MultiInstanceIndex.Value);
            spawnEntry = new ActivityInstanceEntry(
                spawnId, spawn.Activity.ActivityId, State.Id, spawn.ScopeId, spawn.MultiInstanceIndex.Value);
        }
        else
        {
            var spawnVarsId = await activityContext.GetVariablesStateId();
            await spawnInstance.SetVariablesId(spawnVarsId);
            spawnEntry = new ActivityInstanceEntry(spawnId, spawn.Activity.ActivityId, State.Id, spawn.ScopeId);
        }

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

                // MI iterations don't transition to next activities — handled by scope completion
                if (entry.MultiInstanceIndex is not null)
                {
                    LogMultiInstanceIterationCompleted(entry.MultiInstanceIndex.Value, entry.ActivityId);
                    continue;
                }

                var scopeDefinition = definition.GetScopeForActivity(entry.ActivityId);
                var currentActivity = scopeDefinition.GetActivity(entry.ActivityId);

                // MI host completed with empty collection — set empty output variable
                if (currentActivity is MultiInstanceActivity miEmpty
                    && entry.MultiInstanceTotal == 0
                    && miEmpty.OutputCollection is not null)
                {
                    var hostVarId = await activityInstance.GetVariablesStateId();
                    dynamic emptyOutput = new System.Dynamic.ExpandoObject();
                    ((IDictionary<string, object?>)emptyOutput)[miEmpty.OutputCollection] = new List<object?>();
                    State.MergeState(hostVarId, (System.Dynamic.ExpandoObject)emptyOutput);
                }

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

                var isSubProcess = activity is SubProcess;
                var isMultiInstanceHost = activity is MultiInstanceActivity
                    && entry.MultiInstanceIndex is null;

                if (!isSubProcess && !isMultiInstanceHost) continue;

                var scopeEntries = State.Entries.Where(e => e.ScopeId == entry.ActivityInstanceId).ToList();
                if (scopeEntries.Count == 0) continue;

                if (isMultiInstanceHost)
                {
                    var completedMiResult = await TryCompleteMultiInstanceHost(
                        entry, (MultiInstanceActivity)activity, scopeDefinition, scopeEntries);
                    if (completedMiResult)
                        anyCompleted = true;
                    continue;
                }

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

    private async Task<bool> TryCompleteMultiInstanceHost(
        ActivityInstanceEntry hostEntry,
        MultiInstanceActivity mi,
        IWorkflowDefinition scopeDefinition,
        List<ActivityInstanceEntry> scopeEntries)
    {
        var completedIterations = scopeEntries.Where(e => e.IsCompleted).ToList();
        var activeIterations = scopeEntries.Where(e => !e.IsCompleted).ToList();
        var total = hostEntry.MultiInstanceTotal
            ?? throw new InvalidOperationException("MI host entry missing MultiInstanceTotal");

        // If there are active iterations, wait for them
        if (activeIterations.Count > 0)
            return false;

        // All spawned iterations are done
        if (completedIterations.Count < total)
        {
            // Sequential mode: spawn next iteration
            var nextIndex = completedIterations.Count;
            LogMultiInstanceNextIteration(nextIndex, mi.ActivityId);

            var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(hostEntry.ActivityInstanceId);
            var parentVariablesId = await hostGrain.GetVariablesStateId();

            // Resolve collection item for next iteration
            object? iterationItem = null;
            if (mi.InputCollection is not null && mi.InputDataItem is not null)
            {
                var collectionVar = await GetVariable(parentVariablesId, mi.InputCollection);
                if (collectionVar is IList<object> list)
                    iterationItem = list[nextIndex];
                else if (collectionVar is System.Collections.IEnumerable enumerable and not string)
                    iterationItem = enumerable.Cast<object>().ElementAt(nextIndex);
            }

            // Create child variable scope with iteration variables
            var childVariablesId = State.AddChildVariableState(parentVariablesId);
            dynamic iterVars = new System.Dynamic.ExpandoObject();
            var iterDict = (IDictionary<string, object?>)iterVars;
            iterDict["loopCounter"] = nextIndex;
            if (iterationItem is not null && mi.InputDataItem is not null)
                iterDict[mi.InputDataItem] = iterationItem;
            State.MergeState(childVariablesId, (System.Dynamic.ExpandoObject)iterVars);

            var iterationInstanceId = Guid.NewGuid();
            var iterationGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterationInstanceId);
            await iterationGrain.SetActivity(mi.ActivityId, mi.InnerActivity.GetType().Name);
            await iterationGrain.SetVariablesId(childVariablesId);
            await iterationGrain.SetMultiInstanceIndex(nextIndex);

            var iterationEntry = new ActivityInstanceEntry(
                iterationInstanceId, mi.ActivityId, State.Id, hostEntry.ActivityInstanceId, nextIndex);
            State.AddEntries([iterationEntry]);

            return false; // host not completed yet
        }

        // All iterations done — aggregate output
        if (mi.OutputDataItem is not null && mi.OutputCollection is not null)
        {
            var iterationEntries = scopeEntries
                .Where(e => e.MultiInstanceIndex.HasValue)
                .OrderBy(e => e.MultiInstanceIndex!.Value)
                .ToList();

            var outputList = new List<object?>();
            foreach (var iterEntry in iterationEntries)
            {
                var iterGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterEntry.ActivityInstanceId);
                var iterVarId = await iterGrain.GetVariablesStateId();
                var iterVarState = State.VariableStates.FirstOrDefault(v => v.Id == iterVarId);
                var outputValue = iterVarState is not null
                    ? State.GetVariable(iterVarId, mi.OutputDataItem)
                    : null;
                outputList.Add(outputValue);
            }

            var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(hostEntry.ActivityInstanceId);
            var hostVarId = await hostGrain.GetVariablesStateId();
            dynamic outputVars = new System.Dynamic.ExpandoObject();
            ((IDictionary<string, object?>)outputVars)[mi.OutputCollection] = outputList;
            State.MergeState(hostVarId, (System.Dynamic.ExpandoObject)outputVars);
        }

        // Clean up child variable scopes
        var childVarIds = new List<Guid>();
        foreach (var iterEntry in scopeEntries.Where(e => e.MultiInstanceIndex.HasValue))
        {
            var iterGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterEntry.ActivityInstanceId);
            childVarIds.Add(await iterGrain.GetVariablesStateId());
        }
        State.RemoveVariableStates(childVarIds);

        // Complete host
        var hostGrainForComplete = _grainFactory.GetGrain<IActivityInstanceGrain>(hostEntry.ActivityInstanceId);
        await hostGrainForComplete.Complete();
        LogMultiInstanceScopeCompleted(mi.ActivityId);

        var nextActivities = await mi.GetNextActivities(this, hostGrainForComplete, scopeDefinition);

        var completedEntries = new List<ActivityInstanceEntry> { hostEntry };
        var newEntries = new List<ActivityInstanceEntry>();

        foreach (var nextActivity in nextActivities)
        {
            var newEntry = await CreateNextActivityEntry(mi, hostGrainForComplete, nextActivity, hostEntry.ScopeId);
            newEntries.Add(newEntry);
        }

        State.CompleteEntries(completedEntries);
        State.AddEntries(newEntries);
        return true;
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
