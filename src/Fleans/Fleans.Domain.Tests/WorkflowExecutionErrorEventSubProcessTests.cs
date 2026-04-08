using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionErrorEventSubProcessTests
{
    private static EventSubProcess BuildErrorEventSubProcess(string id, string? errorCode)
    {
        var start = new ErrorStartEvent($"{id}_errStart", errorCode);
        var handler = new ScriptTask($"{id}_handler", "// noop");
        var end = new EndEvent($"{id}_errEnd");
        return new EventSubProcess(id)
        {
            Activities = [start, handler, end],
            SequenceFlows =
            [
                new SequenceFlow($"{id}_sf1", start, handler),
                new SequenceFlow($"{id}_sf2", handler, end)
            ],
            IsInterrupting = true
        };
    }

    private static (WorkflowExecution execution, WorkflowInstanceState state)
        CreateStarted(List<Activity> activities, List<SequenceFlow> flows)
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
        return (execution, state);
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcessAtRoot_SpawnsHandlerAndCancelsSiblings()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var esp = BuildErrorEventSubProcess("evtSub1", "500");

        var (execution, state) = CreateStarted(
            [start, task, end, esp],
            [new("seq1", start, task), new("seq2", task, end)]);

        // Drive start -> task
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(task)])
        ]);
        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(1, events.OfType<ActivityFailed>().Count());

        var spawned = events.OfType<ActivitySpawned>().ToList();
        var espSpawn = spawned.Single(s => s.ActivityId == "evtSub1");
        Assert.AreEqual(nameof(EventSubProcess), espSpawn.ActivityType);
        Assert.AreEqual(taskEntry.ScopeId, espSpawn.ScopeId);

        var errStartSpawn = spawned.Single(s => s.ActivityId == "evtSub1_errStart");
        Assert.AreEqual(espSpawn.ActivityInstanceId, errStartSpawn.ScopeId);

        var childScope = events.OfType<ChildVariableScopeCreated>().Single();
        Assert.AreEqual(taskEntry.VariablesId, childScope.ParentScopeId);
        Assert.AreEqual(childScope.ScopeId, errStartSpawn.VariablesId);
    }

    [TestMethod]
    public void FailActivity_BoundaryErrorEventBeatsErrorEventSubProcess()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var boundaryError = new BoundaryErrorEvent("boundary-error1", "task1", "500");
        var errorHandler = new ScriptTask("errorHandler1", "return 'handled';");
        var esp = BuildErrorEventSubProcess("evtSub1", "500");

        var (execution, state) = CreateStarted(
            [start, task, end, boundaryError, errorHandler, esp],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq3", boundaryError, errorHandler)
            ]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(task)])
        ]);
        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var events = execution.GetUncommittedEvents();
        var spawnedIds = events.OfType<ActivitySpawned>().Select(s => s.ActivityId).ToList();
        CollectionAssert.Contains(spawnedIds, "boundary-error1");
        CollectionAssert.DoesNotContain(spawnedIds, "evtSub1");
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcessInOuterScope_CancelsInnerSubProcessEntry()
    {
        var innerStart = new StartEvent("innerStart");
        var task1 = new ScriptTask("task1", "return 1;");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [innerStart, task1],
            SequenceFlows = [new SequenceFlow("inner_sf1", innerStart, task1)]
        };

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var esp = BuildErrorEventSubProcess("evtSubOuter", "500");

        var (execution, state) = CreateStarted(
            [start, sub1, end, esp],
            [new("seq1", start, sub1), new("seq2", sub1, end)]);

        // start -> sub1
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(sub1)])
        ]);

        var subEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub1");
        execution.MarkExecuting(subEntry.ActivityInstanceId);

        // Open subprocess -> spawns innerStart inside scope
        execution.ProcessCommands(
            [new OpenSubProcessCommand(sub1, subEntry.VariablesId)],
            subEntry.ActivityInstanceId);

        var innerStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "innerStart");
        execution.MarkExecuting(innerStartEntry.ActivityInstanceId);
        execution.MarkCompleted(innerStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(innerStartEntry.ActivityInstanceId, "innerStart",
                [new ActivityTransition(task1)])
        ]);
        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var events = execution.GetUncommittedEvents();

        // sub1 entry must be cancelled
        var cancelled = events.OfType<ActivityCancelled>().ToList();
        Assert.IsTrue(cancelled.Any(c => c.ActivityInstanceId == subEntry.ActivityInstanceId),
            "expected sub1 entry to be cancelled");

        // evtSubOuter spawn must be at root scope (= subEntry.ScopeId, which is null)
        var espSpawn = events.OfType<ActivitySpawned>().Single(s => s.ActivityId == "evtSubOuter");
        Assert.AreEqual(subEntry.ScopeId, espSpawn.ScopeId);
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcess_HandlerCompletes_HostCompletes()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var esp = BuildErrorEventSubProcess("evtSub1", "500");

        var (execution, state) = CreateStarted(
            [start, task, end, esp],
            [new("seq1", start, task), new("seq2", task, end)]);

        // Drive start -> task
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(task)])
        ]);
        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        // Drive errStart -> handler -> errEnd inside the EventSubProcess scope
        var errStart = (ErrorStartEvent)esp.Activities.First(a => a is ErrorStartEvent);
        var handler = (ScriptTask)esp.Activities.First(a => a.ActivityId == "evtSub1_handler");
        var errEnd = (EndEvent)esp.Activities.First(a => a is EndEvent);

        var errStartEntry = state.GetActiveActivities().First(e => e.ActivityId == errStart.ActivityId);
        execution.MarkExecuting(errStartEntry.ActivityInstanceId);
        execution.MarkCompleted(errStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(errStartEntry.ActivityInstanceId, errStart.ActivityId,
                [new ActivityTransition(handler)])
        ]);

        var handlerEntry = state.GetActiveActivities().First(e => e.ActivityId == handler.ActivityId);
        execution.MarkExecuting(handlerEntry.ActivityInstanceId);
        execution.MarkCompleted(handlerEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(handlerEntry.ActivityInstanceId, handler.ActivityId,
                [new ActivityTransition(errEnd)])
        ]);

        var endEntry = state.GetActiveActivities().First(e => e.ActivityId == errEnd.ActivityId);
        execution.MarkExecuting(endEntry.ActivityInstanceId);
        execution.MarkCompleted(endEntry.ActivityInstanceId, new ExpandoObject());

        execution.ClearUncommittedEvents();
        var (_, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        var espHostEntry = state.Entries.First(e => e.ActivityId == "evtSub1");
        Assert.IsTrue(completedHostIds.Contains(espHostEntry.ActivityInstanceId),
            "expected EventSubProcess host to be completed");

        var completedEvents = execution.GetUncommittedEvents().OfType<ActivityCompleted>().ToList();
        Assert.IsTrue(completedEvents.Any(c => c.ActivityInstanceId == espHostEntry.ActivityInstanceId));
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcess_LongRunningSiblingUserTask_IsCancelled()
    {
        var start = new StartEvent("start1");
        var fork = new ParallelGateway("fork1", true);
        var failingTask = new ScriptTask("failingTask", "throw new Exception();");
        var siblingTask = new TaskActivity("siblingTask");
        var join = new ParallelGateway("join1", false);
        var end = new EndEvent("end1");
        var esp = BuildErrorEventSubProcess("evtSub1", "500");

        var (execution, state) = CreateStarted(
            [start, fork, failingTask, siblingTask, join, end, esp],
            [
                new("seq1", start, fork),
                new("seq2", fork, failingTask),
                new("seq3", fork, siblingTask),
                new("seq4", failingTask, join),
                new("seq5", siblingTask, join),
                new("seq6", join, end)
            ]);

        // start -> fork
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(fork)])
        ]);

        // fork -> [failingTask, siblingTask]
        var forkEntry = state.GetActiveActivities().First(e => e.ActivityId == "fork1");
        execution.MarkExecuting(forkEntry.ActivityInstanceId);
        execution.MarkCompleted(forkEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(forkEntry.ActivityInstanceId, "fork1",
            [
                new ActivityTransition(failingTask),
                new ActivityTransition(siblingTask)
            ])
        ]);

        var failingEntry = state.GetActiveActivities().First(e => e.ActivityId == "failingTask");
        var siblingEntry = state.GetActiveActivities().First(e => e.ActivityId == "siblingTask");
        execution.MarkExecuting(failingEntry.ActivityInstanceId);
        execution.MarkExecuting(siblingEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        execution.FailActivity("failingTask", failingEntry.ActivityInstanceId, new Exception("boom"));

        var events = execution.GetUncommittedEvents();
        var cancelled = events.OfType<ActivityCancelled>().ToList();
        Assert.IsTrue(cancelled.Any(c => c.ActivityInstanceId == siblingEntry.ActivityInstanceId),
            "expected sibling task to be cancelled");
    }

    /// <summary>
    /// When an activity inside an error event sub-process fails, the engine must NOT
    /// re-trigger the same error event sub-process (BPMN spec: an event sub-process only
    /// catches errors from the enclosing parent scope, not from within itself).
    /// Regression guard for bug #280: infinite loop when handlerTask inside the error
    /// event sub-process throws an error matching the sub-process's own trigger code.
    /// </summary>
    [TestMethod]
    public void FailActivity_InsideErrorEventSubProcess_DoesNotRetriggerSameSubProcess()
    {
        var start = new StartEvent("start");
        var failingTask = new ScriptTask("failingTask", "throw;");
        var normalEnd = new EndEvent("normalEnd");
        var esp = BuildErrorEventSubProcess("errorEventSub", "500");

        var (execution, state) = CreateStarted(
            [start, failingTask, normalEnd, esp],
            [
                new SequenceFlow("sf1", start, failingTask),
                new SequenceFlow("sf2", failingTask, normalEnd)
            ]);

        // Spawn and execute the failing task
        var startEntry = state.GetActiveActivities().First(e => e.ActivityId == "start");
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start",
                [new ActivityTransition(failingTask)])
        ]);

        var failingEntry = state.GetActiveActivities().First(e => e.ActivityId == "failingTask");
        execution.MarkExecuting(failingEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // failingTask fails → errorEventSub should activate (errStart + handler spawned)
        execution.FailActivity("failingTask", failingEntry.ActivityInstanceId,
            new Exception("boom") { Data = { ["ErrorCode"] = 500 } });

        var afterOuter = execution.GetUncommittedEvents();
        var spawnedAfterOuter = afterOuter.OfType<ActivitySpawned>().Select(e => e.ActivityId).ToList();
        Assert.IsTrue(spawnedAfterOuter.Contains("errorEventSub"),
            "expected errorEventSub to be spawned after failingTask fails");
        Assert.IsTrue(spawnedAfterOuter.Contains("errorEventSub_errStart"),
            "expected errStart to be spawned inside errorEventSub");

        // Now simulate handlerTask inside the ESP failing with the same error code.
        // The engine must NOT re-trigger the ESP — doing so would cause an infinite loop.
        var espEntry = state.GetActiveActivities().First(e => e.ActivityId == "errorEventSub");
        var errStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "errorEventSub_errStart");
        execution.MarkExecuting(errStartEntry.ActivityInstanceId);
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(errStartEntry.ActivityInstanceId, "errorEventSub_errStart",
                [new ActivityTransition(new ScriptTask("errorEventSub_handler", "// noop"))])
        ]);

        var handlerEntry = state.GetActiveActivities().FirstOrDefault(e => e.ActivityId == "errorEventSub_handler");
        if (handlerEntry is not null)
        {
            execution.MarkExecuting(handlerEntry.ActivityInstanceId);
            execution.ClearUncommittedEvents();

            // Handler fails with same error code 500 — must NOT re-spawn errorEventSub
            execution.FailActivity("errorEventSub_handler", handlerEntry.ActivityInstanceId,
                new Exception("handler failed") { Data = { ["ErrorCode"] = 500 } });

            var afterInner = execution.GetUncommittedEvents();
            var espRespawned = afterInner.OfType<ActivitySpawned>()
                .Any(e => e.ActivityId == "errorEventSub");
            Assert.IsFalse(espRespawned,
                "errorEventSub must NOT be re-triggered when its own handler activity fails (infinite loop guard)");
        }
    }
}
