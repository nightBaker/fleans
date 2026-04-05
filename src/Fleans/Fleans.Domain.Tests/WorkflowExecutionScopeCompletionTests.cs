using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionScopeCompletionTests
{
    // --- Helpers ---

    private static (WorkflowExecution execution, WorkflowInstanceState state, WorkflowDefinition definition)
        CreateStartedExecution(
            List<Activity> activities,
            List<SequenceFlow> flows)
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = activities,
            SequenceFlows = flows,
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        return (execution, state, definition);
    }

    /// <summary>
    /// Advances the workflow through: start event completed -> host activity spawned and executing.
    /// Returns the host entry and execution ready for scope completion testing.
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry hostEntry)
        CreateWithExecutingHost(
            List<Activity> activities,
            List<SequenceFlow> flows,
            Activity hostActivity)
    {
        var (execution, state, _) = CreateStartedExecution(activities, flows);

        // Complete start event
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(hostActivity)])
        ]);

        var hostEntry = state.GetActiveActivities().First(e => e.ActivityId == hostActivity.ActivityId);
        execution.MarkExecuting(hostEntry.ActivityInstanceId);

        return (execution, state, hostEntry);
    }

    // ===== SubProcess Scope Completion Tests =====

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_AllChildrenCompleted_ShouldCompleteHost()
    {
        // Build: start -> subProcess(subStart -> subEnd) -> end
        var subStart = new StartEvent("subStart1");
        var subEnd = new EndEvent("subEnd1");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subEnd],
            SequenceFlows = [new SequenceFlow("subSeq1", subStart, subEnd)]
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, subProcess, end],
            [new("seq1", start, subProcess), new("seq2", subProcess, end)],
            subProcess);

        // Process the OpenSubProcessCommand to spawn subStart inside the scope
        var parentVarId = hostEntry.VariablesId;
        execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, parentVarId)],
            hostEntry.ActivityInstanceId);

        // Find the spawned sub-start entry
        var subStartEntry = state.Entries.First(e => e.ActivityId == "subStart1");
        Assert.AreEqual(hostEntry.ActivityInstanceId, subStartEntry.ScopeId);

        // Execute and complete the sub-start
        execution.MarkExecuting(subStartEntry.ActivityInstanceId);
        execution.MarkCompleted(subStartEntry.ActivityInstanceId, new ExpandoObject());

        // Resolve transitions: subStart -> subEnd
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(subStartEntry.ActivityInstanceId, "subStart1",
                [new ActivityTransition(subEnd)])
        ]);

        // Execute and complete the sub-end
        var subEndEntry = state.Entries.First(e => e.ActivityId == "subEnd1");
        execution.MarkExecuting(subEndEntry.ActivityInstanceId);
        execution.MarkCompleted(subEndEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Now scope completion should detect the subprocess is done
        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        // Host should be completed
        Assert.AreEqual(1, completedHostIds.Count);
        Assert.AreEqual(hostEntry.ActivityInstanceId, completedHostIds[0]);
        Assert.IsTrue(hostEntry.IsCompleted);

        // Should have emitted ActivityCompleted for the host
        var events = execution.GetUncommittedEvents();
        var hostCompleted = events.OfType<ActivityCompleted>()
            .FirstOrDefault(e => e.ActivityInstanceId == hostEntry.ActivityInstanceId);
        Assert.IsNotNull(hostCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ChildrenNotAllCompleted_ShouldNotCompleteHost()
    {
        var subStart = new StartEvent("subStart1");
        var subTask = new ScriptTask("subTask1", "return 1;");
        var subEnd = new EndEvent("subEnd1");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subTask, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("subSeq1", subStart, subTask),
                new SequenceFlow("subSeq2", subTask, subEnd)
            ]
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, subProcess, end],
            [new("seq1", start, subProcess), new("seq2", subProcess, end)],
            subProcess);

        // Spawn sub-start and complete it
        execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, hostEntry.VariablesId)],
            hostEntry.ActivityInstanceId);

        var subStartEntry = state.Entries.First(e => e.ActivityId == "subStart1");
        execution.MarkExecuting(subStartEntry.ActivityInstanceId);
        execution.MarkCompleted(subStartEntry.ActivityInstanceId, new ExpandoObject());

        // Resolve transition: subStart -> subTask (but don't complete subTask)
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(subStartEntry.ActivityInstanceId, "subStart1",
                [new ActivityTransition(subTask)])
        ]);
        execution.ClearUncommittedEvents();

        // Scope completion should NOT complete host because subTask is still active
        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(0, completedHostIds.Count);
        Assert.IsFalse(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_NoScopeEntries_ShouldNotCompleteHost()
    {
        // SubProcess host is active but no children spawned yet
        var subStart = new StartEvent("subStart1");
        var subEnd = new EndEvent("subEnd1");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subEnd],
            SequenceFlows = [new SequenceFlow("subSeq1", subStart, subEnd)]
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, subProcess, end],
            [new("seq1", start, subProcess), new("seq2", subProcess, end)],
            subProcess);

        // Do NOT process OpenSubProcessCommand, so no children exist
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(0, completedHostIds.Count);
        Assert.IsFalse(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_NestedSubProcess_ShouldCompleteInnerThenOuter()
    {
        // Build: start -> outerSub(outerStart -> innerSub(innerStart -> innerEnd) -> outerEnd) -> end
        var innerStart = new StartEvent("innerStart");
        var innerEnd = new EndEvent("innerEnd");
        var innerSub = new SubProcess("innerSub")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("innerSeq1", innerStart, innerEnd)]
        };

        var outerStart = new StartEvent("outerStart");
        var outerEnd = new EndEvent("outerEnd");
        var outerSub = new SubProcess("outerSub")
        {
            Activities = [outerStart, innerSub, outerEnd],
            SequenceFlows =
            [
                new SequenceFlow("outerSeq1", outerStart, innerSub),
                new SequenceFlow("outerSeq2", innerSub, outerEnd)
            ]
        };

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, outerHostEntry) = CreateWithExecutingHost(
            [start, outerSub, end],
            [new("seq1", start, outerSub), new("seq2", outerSub, end)],
            outerSub);

        // Open outer subprocess
        execution.ProcessCommands(
            [new OpenSubProcessCommand(outerSub, outerHostEntry.VariablesId)],
            outerHostEntry.ActivityInstanceId);

        // Complete outerStart
        var outerStartEntry = state.Entries.First(e => e.ActivityId == "outerStart");
        execution.MarkExecuting(outerStartEntry.ActivityInstanceId);
        execution.MarkCompleted(outerStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(outerStartEntry.ActivityInstanceId, "outerStart",
                [new ActivityTransition(innerSub)])
        ]);

        // Open inner subprocess (spawned by outerSub resolution)
        var innerSubEntry = state.GetActiveActivities().First(e => e.ActivityId == "innerSub");
        execution.MarkExecuting(innerSubEntry.ActivityInstanceId);
        execution.ProcessCommands(
            [new OpenSubProcessCommand(innerSub, innerSubEntry.VariablesId)],
            innerSubEntry.ActivityInstanceId);

        // Complete innerStart
        var innerStartEntry = state.Entries.First(e => e.ActivityId == "innerStart");
        execution.MarkExecuting(innerStartEntry.ActivityInstanceId);
        execution.MarkCompleted(innerStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(innerStartEntry.ActivityInstanceId, "innerStart",
                [new ActivityTransition(innerEnd)])
        ]);

        // Complete innerEnd
        var innerEndEntry = state.Entries.First(e => e.ActivityId == "innerEnd");
        execution.MarkExecuting(innerEndEntry.ActivityInstanceId);
        execution.MarkCompleted(innerEndEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // First scope completion should complete innerSub host
        var (effects1, completedIds1, _) = execution.CompleteFinishedSubProcessScopes();

        // Inner sub should be completed, and since that makes all outer scope children
        // completed, the outer sub should also be completed in the same call (loop detects it)
        Assert.IsTrue(innerSubEntry.IsCompleted);
        Assert.IsTrue(outerHostEntry.IsCompleted);
        Assert.AreEqual(2, completedIds1.Count);
        Assert.IsTrue(completedIds1.Contains(innerSubEntry.ActivityInstanceId));
        Assert.IsTrue(completedIds1.Contains(outerHostEntry.ActivityInstanceId));
    }

    // ===== MultiInstance Parallel Completion Tests =====

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ParallelMI_AllIterationsDone_ShouldCompleteHost()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 3);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        // Set MultiInstanceTotal on host
        hostEntry.SetMultiInstanceTotal(3);

        // Spawn 3 iteration entries (simulating what ProcessCommands would do)
        for (var i = 0; i < 3; i++)
        {
            var childScopeId = Guid.NewGuid();
            state.AddChildVariableState(childScopeId, hostEntry.VariablesId);
            var iterVars = new ExpandoObject();
            ((IDictionary<string, object?>)iterVars)["loopCounter"] = i;
            state.MergeState(childScopeId, iterVars);

            var iterEntry = new ActivityInstanceEntry(
                Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, i);
            iterEntry.SetActivityType("ScriptTask");
            iterEntry.SetVariablesId(childScopeId);
            state.AddEntries([iterEntry]);
        }

        // Complete all 3 iterations
        var iterations = state.Entries
            .Where(e => e.MultiInstanceIndex.HasValue && e.ScopeId == hostEntry.ActivityInstanceId)
            .ToList();
        foreach (var iter in iterations)
        {
            iter.Execute();
            iter.Complete();
        }
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(1, completedHostIds.Count);
        Assert.AreEqual(hostEntry.ActivityInstanceId, completedHostIds[0]);
        Assert.IsTrue(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ParallelMI_SomeIterationsActive_ShouldNotCompleteHost()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 3);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(3);

        // Spawn 3 iterations, complete only 2
        for (var i = 0; i < 3; i++)
        {
            var childScopeId = Guid.NewGuid();
            state.AddChildVariableState(childScopeId, hostEntry.VariablesId);

            var iterEntry = new ActivityInstanceEntry(
                Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, i);
            iterEntry.SetActivityType("ScriptTask");
            iterEntry.SetVariablesId(childScopeId);
            state.AddEntries([iterEntry]);

            if (i < 2)
            {
                iterEntry.Execute();
                iterEntry.Complete();
            }
        }
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(0, completedHostIds.Count);
        Assert.IsFalse(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ParallelMI_OutputAggregation_ShouldCollectOutputValues()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity(
            "mi1", innerTask,
            IsSequential: false,
            LoopCardinality: 3,
            OutputDataItem: "result",
            OutputCollection: "results");
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(3);

        // Spawn 3 iterations with output variables
        for (var i = 0; i < 3; i++)
        {
            var childScopeId = Guid.NewGuid();
            state.AddChildVariableState(childScopeId, hostEntry.VariablesId);

            // Set the "result" variable on each iteration's scope
            var resultVars = new ExpandoObject();
            ((IDictionary<string, object?>)resultVars)["result"] = $"value-{i}";
            state.MergeState(childScopeId, resultVars);

            var iterEntry = new ActivityInstanceEntry(
                Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, i);
            iterEntry.SetActivityType("ScriptTask");
            iterEntry.SetVariablesId(childScopeId);
            state.AddEntries([iterEntry]);

            iterEntry.Execute();
            iterEntry.Complete();
        }
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(1, completedHostIds.Count);

        // Check that the output collection was aggregated into the host's variable scope
        var hostVars = state.GetMergedVariables(hostEntry.VariablesId);
        var hostDict = (IDictionary<string, object?>)hostVars;
        Assert.IsTrue(hostDict.ContainsKey("results"));
        var resultsList = (List<object?>)hostDict["results"]!;
        Assert.AreEqual(3, resultsList.Count);
        Assert.AreEqual("value-0", resultsList[0]);
        Assert.AreEqual("value-1", resultsList[1]);
        Assert.AreEqual("value-2", resultsList[2]);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ParallelMI_ShouldCleanupChildVariableScopes()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 2);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(2);

        var childScopeIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var childScopeId = Guid.NewGuid();
            childScopeIds.Add(childScopeId);
            state.AddChildVariableState(childScopeId, hostEntry.VariablesId);

            var iterEntry = new ActivityInstanceEntry(
                Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, i);
            iterEntry.SetActivityType("ScriptTask");
            iterEntry.SetVariablesId(childScopeId);
            state.AddEntries([iterEntry]);

            iterEntry.Execute();
            iterEntry.Complete();
        }

        // Track initial variable scope count
        var initialScopeCount = state.VariableStates.Count;
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(1, completedHostIds.Count);

        // Child scopes should be removed
        foreach (var childId in childScopeIds)
        {
            Assert.IsFalse(state.VariableStates.Any(vs => vs.Id == childId),
                $"Child variable scope {childId} should have been removed");
        }

        // Verify VariableScopesRemoved event was emitted
        var events = execution.GetUncommittedEvents();
        var removedEvent = events.OfType<VariableScopesRemoved>().Single();
        CollectionAssert.AreEquivalent(childScopeIds, removedEvent.ScopeIds.ToList());
    }

    // ===== MultiInstance Sequential Completion Tests =====

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_SequentialMI_FirstIterationDone_ShouldSpawnNext()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: true, LoopCardinality: 3);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(3);

        // Spawn first iteration (index 0) and complete it
        var childScopeId = Guid.NewGuid();
        state.AddChildVariableState(childScopeId, hostEntry.VariablesId);
        var iterVars = new ExpandoObject();
        ((IDictionary<string, object?>)iterVars)["loopCounter"] = 0;
        state.MergeState(childScopeId, iterVars);

        var iterEntry = new ActivityInstanceEntry(
            Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, 0);
        iterEntry.SetActivityType("ScriptTask");
        iterEntry.SetVariablesId(childScopeId);
        state.AddEntries([iterEntry]);
        iterEntry.Execute();
        iterEntry.Complete();
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        // Host should NOT be completed (only 1 of 3 done)
        Assert.AreEqual(0, completedHostIds.Count);
        Assert.IsFalse(hostEntry.IsCompleted);

        // Should have spawned next iteration (index 1)
        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("mi1", spawned.ActivityId);
        Assert.AreEqual(1, spawned.MultiInstanceIndex);
        Assert.AreEqual(hostEntry.ActivityInstanceId, spawned.ScopeId);

        // New entry should exist in state
        var newIterEntry = state.Entries.First(e =>
            e.MultiInstanceIndex == 1 && e.ScopeId == hostEntry.ActivityInstanceId);
        Assert.IsFalse(newIterEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_SequentialMI_AllIterationsDone_ShouldCompleteHost()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: true, LoopCardinality: 2);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(2);

        // Spawn and complete both iterations
        for (var i = 0; i < 2; i++)
        {
            var childScopeId = Guid.NewGuid();
            state.AddChildVariableState(childScopeId, hostEntry.VariablesId);

            var iterEntry = new ActivityInstanceEntry(
                Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, i);
            iterEntry.SetActivityType("ScriptTask");
            iterEntry.SetVariablesId(childScopeId);
            state.AddEntries([iterEntry]);

            iterEntry.Execute();
            iterEntry.Complete();
        }
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(1, completedHostIds.Count);
        Assert.AreEqual(hostEntry.ActivityInstanceId, completedHostIds[0]);
        Assert.IsTrue(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_SequentialMI_WithInputCollection_ShouldPassItemToNextIteration()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity(
            "mi1", innerTask,
            IsSequential: true,
            InputCollection: "items",
            InputDataItem: "item");
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(3);

        // Set up the input collection on the host's variable scope
        var collectionVars = new ExpandoObject();
        ((IDictionary<string, object?>)collectionVars)["items"] =
            new List<object> { "alpha", "beta", "gamma" };
        state.MergeState(hostEntry.VariablesId, collectionVars);

        // Spawn and complete first iteration (index 0)
        var childScopeId = Guid.NewGuid();
        state.AddChildVariableState(childScopeId, hostEntry.VariablesId);
        var iterVars = new ExpandoObject();
        var iterDict = (IDictionary<string, object?>)iterVars;
        iterDict["loopCounter"] = 0;
        iterDict["item"] = "alpha";
        state.MergeState(childScopeId, iterVars);

        var iterEntry = new ActivityInstanceEntry(
            Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, 0);
        iterEntry.SetActivityType("ScriptTask");
        iterEntry.SetVariablesId(childScopeId);
        state.AddEntries([iterEntry]);
        iterEntry.Execute();
        iterEntry.Complete();
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        // Should spawn index 1 with "beta" as the item
        var events = execution.GetUncommittedEvents();
        var varsMerged = events.OfType<VariablesMerged>().Single();
        var mergedDict = (IDictionary<string, object?>)varsMerged.Variables;
        Assert.AreEqual(1, mergedDict["loopCounter"]);
        Assert.AreEqual("beta", mergedDict["item"]);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_MI_HostNotYetExecuted_ShouldNotComplete()
    {
        // MultiInstanceTotal is null (host hasn't been executed yet)
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 2);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        // Do NOT set MultiInstanceTotal — simulates host not yet executed
        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(0, completedHostIds.Count);
        Assert.IsFalse(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_NonScopeActivity_ShouldBeIgnored()
    {
        // Regular activity (not subprocess or MI) should be skipped
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end],
            [new("seq1", start, task), new("seq2", task, end)]);

        // Complete start, spawn task
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(task)])
        ]);
        execution.ClearUncommittedEvents();

        // Should return nothing — task is a regular activity, not a scope
        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(0, completedHostIds.Count);
        Assert.AreEqual(0, effects.Count);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_MI_IterationEntry_ShouldNotBeTreatedAsHost()
    {
        // An iteration entry (MultiInstanceIndex != null) should not be treated as a host
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 2);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(2);

        // Spawn 2 iterations — only complete 1
        for (var i = 0; i < 2; i++)
        {
            var childScopeId = Guid.NewGuid();
            state.AddChildVariableState(childScopeId, hostEntry.VariablesId);

            var iterEntry = new ActivityInstanceEntry(
                Guid.NewGuid(), "mi1", state.Id, hostEntry.ActivityInstanceId, i);
            iterEntry.SetActivityType("ScriptTask");
            iterEntry.SetVariablesId(childScopeId);
            state.AddEntries([iterEntry]);

            if (i == 0)
            {
                iterEntry.Execute();
                iterEntry.Complete();
            }
        }
        execution.ClearUncommittedEvents();

        // Iteration entries should NOT trigger scope completion logic
        // (they are children, not hosts)
        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();
        Assert.AreEqual(0, completedHostIds.Count);
        Assert.IsFalse(hostEntry.IsCompleted);
    }

    // ===== Apply VariableScopesRemoved Tests =====

    [TestMethod]
    public void Apply_VariableScopesRemoved_ShouldRemoveVariableStates()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Create some child variable scopes
        var parentVarId = state.VariableStates.First().Id;
        var child1 = Guid.NewGuid();
        var child2 = Guid.NewGuid();
        state.AddChildVariableState(child1, parentVarId);
        state.AddChildVariableState(child2, parentVarId);

        Assert.AreEqual(3, state.VariableStates.Count); // parent + 2 children

        // Simulate MI completion that removes child scopes via Emit
        // We can't directly call Emit, so let's use the full MI completion flow
        // Instead, verify through the aggregate's scope completion path
        // which calls Emit(new VariableScopesRemoved(...))

        // For a direct test, we can verify through events:
        // The VariableScopesRemoved event is emitted by TryCompleteMultiInstanceHost,
        // and the Apply handler calls _state.RemoveVariableStates.
        // Let's set up a MI scenario to trigger it.

        // Actually, let's verify the apply handler works by setting up a full MI completion
        var innerTask = new ScriptTask("task1", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 1);

        var definition2 = new WorkflowDefinition
        {
            WorkflowId = "wf2",
            Activities = [new StartEvent("s1"), mi, new EndEvent("e1")],
            SequenceFlows =
            [
                new SequenceFlow("sq1", new StartEvent("s1"), mi),
                new SequenceFlow("sq2", mi, new EndEvent("e1"))
            ],
            ProcessDefinitionId = "pd2"
        };
        var state2 = new WorkflowInstanceState();
        var execution2 = new WorkflowExecution(state2, definition2);
        execution2.Start();

        // Complete start
        var startEntry2 = state2.Entries.First();
        execution2.MarkExecuting(startEntry2.ActivityInstanceId);
        execution2.MarkCompleted(startEntry2.ActivityInstanceId, new ExpandoObject());
        execution2.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry2.ActivityInstanceId, "s1",
                [new ActivityTransition(mi)])
        ]);

        var miHost = state2.GetActiveActivities().First(e => e.ActivityId == "mi1");
        execution2.MarkExecuting(miHost.ActivityInstanceId);
        miHost.SetMultiInstanceTotal(1);

        // Create iteration
        var iterScopeId = Guid.NewGuid();
        state2.AddChildVariableState(iterScopeId, miHost.VariablesId);

        var iterEntry = new ActivityInstanceEntry(
            Guid.NewGuid(), "mi1", state2.Id, miHost.ActivityInstanceId, 0);
        iterEntry.SetActivityType("ScriptTask");
        iterEntry.SetVariablesId(iterScopeId);
        state2.AddEntries([iterEntry]);
        iterEntry.Execute();
        iterEntry.Complete();

        var initialScopeCount = state2.VariableStates.Count;
        Assert.IsTrue(state2.VariableStates.Any(vs => vs.Id == iterScopeId));

        execution2.ClearUncommittedEvents();

        execution2.CompleteFinishedSubProcessScopes();

        // The child scope should be removed
        Assert.IsFalse(state2.VariableStates.Any(vs => vs.Id == iterScopeId));
        Assert.AreEqual(initialScopeCount - 1, state2.VariableStates.Count);
    }

    // ===== OpenSubProcess ScopeId Fix Test =====

    [TestMethod]
    public void ProcessCommands_OpenSubProcessCommand_ShouldSetScopeIdToHostActivityInstanceId()
    {
        var subStart = new StartEvent("subStart1");
        var subEnd = new EndEvent("subEnd1");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subEnd],
            SequenceFlows = [new SequenceFlow("subSeq1", subStart, subEnd)]
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, subProcess, end],
            [new("seq1", start, subProcess), new("seq2", subProcess, end)]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(subProcess)])
        ]);

        var subHostEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub1");
        execution.MarkExecuting(subHostEntry.ActivityInstanceId);

        var parentVarId = subHostEntry.VariablesId;
        execution.ClearUncommittedEvents();

        execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, parentVarId)],
            subHostEntry.ActivityInstanceId);

        // The spawned sub-start should have ScopeId == host's ActivityInstanceId
        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(subHostEntry.ActivityInstanceId, spawned.ScopeId);
        Assert.AreEqual("subStart1", spawned.ActivityId);
    }

    // ===== Edge Case: Zero Cardinality =====

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ParallelMI_ZeroCardinality_ShouldCompleteHostImmediately()
    {
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var mi = new MultiInstanceActivity("mi1", innerTask, IsSequential: false, LoopCardinality: 0);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, mi, end],
            [new("seq1", start, mi), new("seq2", mi, end)],
            mi);

        hostEntry.SetMultiInstanceTotal(0);
        execution.ClearUncommittedEvents();

        // No iterations to spawn or complete. completedIterations.Count (0) == total (0)
        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.AreEqual(1, completedHostIds.Count);
        Assert.AreEqual(hostEntry.ActivityInstanceId, completedHostIds[0]);
        Assert.IsTrue(hostEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_ShouldMergeChildVariablesIntoParentScope()
    {
        // Build: start -> subProcess(subStart -> subTask -> subEnd) -> end
        var subStart = new StartEvent("subStart1");
        var subTask = new TaskActivity("subTask1");
        var subEnd = new EndEvent("subEnd1");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subTask, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("subSeq1", subStart, subTask),
                new SequenceFlow("subSeq2", subTask, subEnd)
            ]
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, hostEntry) = CreateWithExecutingHost(
            [start, subProcess, end],
            [new("seq1", start, subProcess), new("seq2", subProcess, end)],
            subProcess);

        // Process the OpenSubProcessCommand to spawn subStart inside the scope
        var parentVarId = hostEntry.VariablesId;
        execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, parentVarId)],
            hostEntry.ActivityInstanceId);

        // Find the spawned sub-start entry and complete it
        var subStartEntry = state.Entries.First(e => e.ActivityId == "subStart1");
        execution.MarkExecuting(subStartEntry.ActivityInstanceId);
        execution.MarkCompleted(subStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(subStartEntry.ActivityInstanceId, "subStart1",
                [new ActivityTransition(subTask)])
        ]);

        // Complete sub-task with variables in the child scope
        var subTaskEntry = state.Entries.First(e => e.ActivityId == "subTask1");
        execution.MarkExecuting(subTaskEntry.ActivityInstanceId);
        dynamic childVars = new ExpandoObject();
        childVars.childVar = "from-child";
        childVars.sharedVar = "child-value";
        execution.MarkCompleted(subTaskEntry.ActivityInstanceId, childVars);
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(subTaskEntry.ActivityInstanceId, "subTask1",
                [new ActivityTransition(subEnd)])
        ]);

        // Complete sub-end
        var subEndEntry = state.Entries.First(e => e.ActivityId == "subEnd1");
        execution.MarkExecuting(subEndEntry.ActivityInstanceId);
        execution.MarkCompleted(subEndEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Record parent scope state before merge
        var parentScope = state.GetVariableState(parentVarId);
        Assert.IsFalse(((IDictionary<string, object>)parentScope.Variables).ContainsKey("childVar"),
            "Parent scope should not have child variable before merge");

        // Act — scope completion should merge child variables into parent
        var (effects, completedHostIds, orphanedScopeIds) = execution.CompleteFinishedSubProcessScopes();

        // Assert — host completed
        Assert.AreEqual(1, completedHostIds.Count);
        Assert.IsTrue(hostEntry.IsCompleted);

        // Assert — child variables merged into parent scope
        var parentDict = (IDictionary<string, object>)parentScope.Variables;
        Assert.IsTrue(parentDict.ContainsKey("childVar"),
            "Child variable should be merged into parent scope");
        Assert.AreEqual("from-child", parentDict["childVar"]);
        Assert.AreEqual("child-value", parentDict["sharedVar"],
            "Child scope value should overwrite parent for shared keys");

        // Assert — child scope collected for deferred removal
        Assert.AreEqual(1, orphanedScopeIds.Count,
            "Child scope should be collected for deferred removal");
    }
}
