using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// Aggregate-level coverage for the slice #C interrupting timer event sub-process
/// runtime. The integration test in <c>EventSubProcessTimerTests</c> covers the
/// happy path end-to-end via TestCluster; these tests pin down the negative and
/// edge cases at the <see cref="WorkflowExecution"/> level so they cannot
/// silently regress.
/// </summary>
[TestClass]
public class WorkflowExecutionTimerEventSubProcessTests
{
    private static EventSubProcess BuildTimerEventSubProcess(
        string id, string duration, string timerStartId,
        TimerType timerType = TimerType.Duration)
    {
        var timerStart = new TimerStartEvent(
            timerStartId, new TimerDefinition(timerType, duration));
        var handler = new ScriptTask($"{id}_handler", "// noop");
        var end = new EndEvent($"{id}_end");
        return new EventSubProcess(id)
        {
            Activities = [timerStart, handler, end],
            SequenceFlows =
            [
                new SequenceFlow($"{id}_sf1", timerStart, handler),
                new SequenceFlow($"{id}_sf2", handler, end),
            ],
            IsInterrupting = true,
        };
    }

    private static EventSubProcess BuildErrorEventSubProcess(string id, string errorCode)
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
                new SequenceFlow($"{id}_sf2", handler, end),
            ],
            IsInterrupting = true,
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
            ProcessDefinitionId = "pd1",
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        return (execution, state);
    }

    [TestMethod]
    public void BuildRootScopeEntryEffects_EmitsRegisterTimer_ForRootEventSubTimer()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var esp = BuildTimerEventSubProcess("evtSub1", "PT5S", "timerStart1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, task, end, esp],
            SequenceFlows = [new("seq1", start, task), new("seq2", task, end)],
            ProcessDefinitionId = "pd1",
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        var effects = execution.MarkExecutionStarted();

        var timerEffect = effects.OfType<RegisterTimerEffect>().Single();
        Assert.AreEqual("timerStart1", timerEffect.TimerActivityId);
        Assert.AreEqual(state.Id, timerEffect.HostActivityInstanceId,
            "Root-scope event-sub timers must use the workflow instance id as their synthetic host id");
        Assert.AreEqual(state.Id, timerEffect.WorkflowInstanceId);
        Assert.AreEqual(TimeSpan.FromSeconds(5), timerEffect.DueTime);
    }

    [TestMethod]
    public void ProcessOpenSubProcess_RegistersEventSubTimer_KeyedToSubProcessHostInstance()
    {
        var innerStart = new StartEvent("innerStart");
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var innerEsp = BuildTimerEventSubProcess("innerEvtSub", "PT7S", "innerTimerStart");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEsp],
            SequenceFlows = [new SequenceFlow("inner_sf1", innerStart, innerTask)],
        };

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state) = CreateStarted(
            [start, sub1, end],
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

        var effects = execution.ProcessCommands(
            [new OpenSubProcessCommand(sub1, subEntry.VariablesId)],
            subEntry.ActivityInstanceId);

        var timerEffect = effects.OfType<RegisterTimerEffect>().Single();
        Assert.AreEqual("innerTimerStart", timerEffect.TimerActivityId);
        Assert.AreEqual(subEntry.ActivityInstanceId, timerEffect.HostActivityInstanceId,
            "Nested event-sub timers must be keyed to the SubProcess host activity instance id");
        Assert.AreEqual(TimeSpan.FromSeconds(7), timerEffect.DueTime);
    }

    [TestMethod]
    public void HandleTimerFired_ActivatesEventSubProcess_AndCancelsSiblings_AtRoot()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var esp = BuildTimerEventSubProcess("evtSub1", "PT5S", "timerStart1");

        var (execution, state) = CreateStarted(
            [start, task, end, esp],
            [new("seq1", start, task), new("seq2", task, end)]);

        // start -> task
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

        var effects = execution.HandleTimerFired("timerStart1", state.Id);

        // Task should be cancelled
        var events = execution.GetUncommittedEvents();
        var cancelled = events.OfType<ActivityCancelled>().ToList();
        Assert.IsTrue(cancelled.Any(c => c.ActivityInstanceId == taskEntry.ActivityInstanceId),
            "Sibling task must be cancelled by the interrupting timer event sub-process");

        // Event sub-process host spawned
        var spawned = events.OfType<ActivitySpawned>().ToList();
        var espSpawn = spawned.Single(s => s.ActivityId == "evtSub1");
        Assert.AreEqual(nameof(EventSubProcess), espSpawn.ActivityType);
        Assert.IsNull(espSpawn.ScopeId);

        // TimerStartEvent spawned inside the ESP scope
        var timerStartSpawn = spawned.Single(s => s.ActivityId == "timerStart1");
        Assert.AreEqual(espSpawn.ActivityInstanceId, timerStartSpawn.ScopeId);

        // Effects should not contain a self-unregister for the timer that just fired
        // (the firing grain deactivates itself).
        Assert.IsFalse(effects.OfType<UnregisterTimerEffect>()
                .Any(u => u.TimerActivityId == "timerStart1"),
            "The firing timer must not be in the peer-unregister list");
    }

    [TestMethod]
    public void HandleTimerFired_InterruptingCycleTimer_FiresButDoesNotReRegister()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var esp = BuildTimerEventSubProcess(
            "evtSub1", "R/PT10S", "timerStart1", TimerType.Cycle);

        var (execution, state) = CreateStarted(
            [start, task, end, esp],
            [new("seq1", start, task), new("seq2", task, end)]);

        // Drive into running task
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

        // Interrupting cycle timer fires successfully (no longer throws)
        var effects = execution.HandleTimerFired("timerStart1", state.Id);

        // ESP handler is spawned
        Assert.IsTrue(state.Entries.Any(e => e.ActivityId == "evtSub1"),
            "EventSubProcess host should be spawned");

        // Interrupting ESP does not re-register cycle timer (peer listeners unregistered)
        Assert.IsFalse(effects.Any(e => e is RegisterTimerEffect),
            "Interrupting cycle timer should not produce a re-registration effect");
    }

    [TestMethod]
    public void CompleteFinishedSubProcessScopes_UnregistersEventSubTimer_OnSubProcessCompletion()
    {
        var innerStart = new StartEvent("innerStart");
        var innerTask = new ScriptTask("innerTask", "return 1;");
        var innerEnd = new EndEvent("innerEnd");
        var innerEsp = BuildTimerEventSubProcess("innerEvtSub", "PT7S", "innerTimerStart");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd, innerEsp],
            SequenceFlows =
            [
                new SequenceFlow("inner_sf1", innerStart, innerTask),
                new SequenceFlow("inner_sf2", innerTask, innerEnd),
            ],
        };

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state) = CreateStarted(
            [start, sub1, end],
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
        execution.ProcessCommands(
            [new OpenSubProcessCommand(sub1, subEntry.VariablesId)],
            subEntry.ActivityInstanceId);

        // Drive innerStart -> innerTask -> innerEnd to drain the SubProcess scope
        var innerStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "innerStart");
        execution.MarkExecuting(innerStartEntry.ActivityInstanceId);
        execution.MarkCompleted(innerStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(innerStartEntry.ActivityInstanceId, "innerStart",
                [new ActivityTransition(innerTask)])
        ]);

        var innerTaskEntry = state.GetActiveActivities().First(e => e.ActivityId == "innerTask");
        execution.MarkExecuting(innerTaskEntry.ActivityInstanceId);
        execution.MarkCompleted(innerTaskEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(innerTaskEntry.ActivityInstanceId, "innerTask",
                [new ActivityTransition(innerEnd)])
        ]);

        var innerEndEntry = state.GetActiveActivities().First(e => e.ActivityId == "innerEnd");
        execution.MarkExecuting(innerEndEntry.ActivityInstanceId);
        execution.MarkCompleted(innerEndEntry.ActivityInstanceId, new ExpandoObject());

        execution.ClearUncommittedEvents();

        var (effects, completedHostIds, _) = execution.CompleteFinishedSubProcessScopes();

        Assert.IsTrue(completedHostIds.Contains(subEntry.ActivityInstanceId),
            "SubProcess host should be marked completed");

        var unregister = effects.OfType<UnregisterTimerEffect>().ToList();
        Assert.IsTrue(unregister.Any(u =>
                u.TimerActivityId == "innerTimerStart"
                && u.HostActivityInstanceId == subEntry.ActivityInstanceId),
            "SubProcess completion must unregister its event-sub timer keyed to the SubProcess host instance");
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcess_UnregistersPeerEventSubTimer_AtRoot()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "throw new Exception();");
        var end = new EndEvent("end1");
        var errorEsp = BuildErrorEventSubProcess("errEvtSub", "500");
        var timerEsp = BuildTimerEventSubProcess("timerEvtSub", "PT5S", "peerTimerStart");

        var (execution, state) = CreateStarted(
            [start, task, end, errorEsp, timerEsp],
            [new("seq1", start, task), new("seq2", task, end)]);

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

        var effects = execution.FailActivity(
            "task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        // Error ESP should be spawned
        var spawned = execution.GetUncommittedEvents().OfType<ActivitySpawned>().ToList();
        Assert.IsTrue(spawned.Any(s => s.ActivityId == "errEvtSub"));

        // Peer timer event-sub timer must be unregistered
        var unregister = effects.OfType<UnregisterTimerEffect>().ToList();
        Assert.IsTrue(unregister.Any(u =>
                u.TimerActivityId == "peerTimerStart"
                && u.HostActivityInstanceId == state.Id),
            "When the error event-sub fires it must unregister peer timer event-sub listeners on the same scope");
    }
}
