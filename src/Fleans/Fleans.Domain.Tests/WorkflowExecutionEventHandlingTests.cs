using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionEventHandlingTests
{
    // --- Helpers ---

    private static (WorkflowExecution execution, WorkflowInstanceState state, WorkflowDefinition definition)
        CreateStartedExecution(
            List<Activity> activities,
            List<SequenceFlow> flows,
            List<MessageDefinition>? messages = null,
            List<SignalDefinition>? signals = null)
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = activities,
            SequenceFlows = flows,
            ProcessDefinitionId = "pd1",
            Messages = messages ?? [],
            Signals = signals ?? []
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        return (execution, state, definition);
    }

    /// <summary>
    /// Creates a started workflow with a timer intermediate catch event that has been
    /// spawned and is executing (ready to receive timer fired).
    /// Layout: start -> timerCatch -> end
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry timerEntry)
        CreateWithExecutingTimerCatch()
    {
        var start = new StartEvent("start1");
        var timerCatch = new TimerIntermediateCatchEvent(
            "timer-catch1", new TimerDefinition(TimerType.Duration, "PT30S"));
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, timerCatch, end],
            [new("seq1", start, timerCatch), new("seq2", timerCatch, end)]);

        // Complete start -> spawn timerCatch
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(timerCatch)])
        ]);

        var timerEntry = state.GetActiveActivities().First(e => e.ActivityId == "timer-catch1");
        execution.MarkExecuting(timerEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        return (execution, state, timerEntry);
    }

    /// <summary>
    /// Creates a started workflow with a message intermediate catch event that has been
    /// spawned and is executing (ready to receive message delivery).
    /// Layout: start -> msgCatch -> end
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry msgEntry)
        CreateWithExecutingMessageCatch()
    {
        var start = new StartEvent("start1");
        var msgCatch = new MessageIntermediateCatchEvent("msg-catch1", "msgDef1");
        var end = new EndEvent("end1");

        var msgDef = new MessageDefinition("msgDef1", "OrderMessage", "= orderId");

        var (execution, state, _) = CreateStartedExecution(
            [start, msgCatch, end],
            [new("seq1", start, msgCatch), new("seq2", msgCatch, end)],
            messages: [msgDef]);

        // Set up variable for correlation
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-123";
        state.MergeState(varsId, vars);

        // Complete start -> spawn msgCatch
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(msgCatch)])
        ]);

        var msgEntry = state.GetActiveActivities().First(e => e.ActivityId == "msg-catch1");
        execution.MarkExecuting(msgEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        return (execution, state, msgEntry);
    }

    /// <summary>
    /// Creates a started workflow with a signal intermediate catch event that has been
    /// spawned and is executing (ready to receive signal delivery).
    /// Layout: start -> sigCatch -> end
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry sigEntry)
        CreateWithExecutingSignalCatch()
    {
        var start = new StartEvent("start1");
        var sigCatch = new SignalIntermediateCatchEvent("sig-catch1", "sigDef1");
        var end = new EndEvent("end1");

        var sigDef = new SignalDefinition("sigDef1", "AlertSignal");

        var (execution, state, _) = CreateStartedExecution(
            [start, sigCatch, end],
            [new("seq1", start, sigCatch), new("seq2", sigCatch, end)],
            signals: [sigDef]);

        // Complete start -> spawn sigCatch
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(sigCatch)])
        ]);

        var sigEntry = state.GetActiveActivities().First(e => e.ActivityId == "sig-catch1");
        execution.MarkExecuting(sigEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        return (execution, state, sigEntry);
    }

    // ===== HandleTimerFired Tests =====

    [TestMethod]
    public void HandleTimerFired_ActiveIntermediateCatch_ShouldCompleteActivity()
    {
        var (execution, state, timerEntry) = CreateWithExecutingTimerCatch();

        var effects = execution.HandleTimerFired("timer-catch1", timerEntry.ActivityInstanceId);

        // Entry should be completed
        Assert.IsTrue(timerEntry.IsCompleted);
        Assert.IsFalse(timerEntry.IsExecuting);

        // Events should include ActivityCompleted
        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(timerEntry.ActivityInstanceId, completed.ActivityInstanceId);
    }

    [TestMethod]
    public void HandleTimerFired_AlreadyCompletedEntry_ShouldReturnEmpty()
    {
        var (execution, state, timerEntry) = CreateWithExecutingTimerCatch();

        // Complete it first
        execution.HandleTimerFired("timer-catch1", timerEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Fire again (stale callback)
        var effects = execution.HandleTimerFired("timer-catch1", timerEntry.ActivityInstanceId);

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    [TestMethod]
    public void HandleTimerFired_InterruptingBoundaryTimer_ShouldCancelAttachedAndSpawnBoundary()
    {
        // Build: start -> task (with boundary timer) -> end
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var boundaryTimer = new BoundaryTimerEvent(
            "boundary-timer1", "task1",
            new TimerDefinition(TimerType.Duration, "PT10S"));
        var end = new EndEvent("end1");
        var handlerTask = new ScriptTask("handler1", "return 'handled';");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryTimer, handlerTask],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq3", boundaryTimer, handlerTask)
            ]);

        // Complete start -> spawn task
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

        // Fire boundary timer — should cancel attached activity and spawn boundary
        var effects = execution.HandleTimerFired("boundary-timer1", taskEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();

        // Attached activity should be cancelled
        var cancelled = events.OfType<ActivityCancelled>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, cancelled.ActivityInstanceId);

        // Boundary event activity should be spawned
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("boundary-timer1", spawned.ActivityId);
        Assert.AreEqual("BoundaryTimerEvent", spawned.ActivityType);
    }

    // ===== HandleMessageDelivery Tests =====

    [TestMethod]
    public void HandleMessageDelivery_ActiveIntermediateCatch_ShouldCompleteActivityWithVariables()
    {
        var (execution, state, msgEntry) = CreateWithExecutingMessageCatch();

        var deliveredVars = new ExpandoObject();
        ((IDictionary<string, object?>)deliveredVars)["orderStatus"] = "confirmed";

        var effects = execution.HandleMessageDelivery(
            "msg-catch1", msgEntry.ActivityInstanceId, deliveredVars);

        // Entry should be completed
        Assert.IsTrue(msgEntry.IsCompleted);
        Assert.IsFalse(msgEntry.IsExecuting);

        // Events should include ActivityCompleted
        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(msgEntry.ActivityInstanceId, completed.ActivityInstanceId);

        // Variables should be merged
        var mergedVars = state.GetMergedVariables(msgEntry.VariablesId);
        var dict = (IDictionary<string, object?>)mergedVars;
        Assert.AreEqual("confirmed", dict["orderStatus"]);
    }

    [TestMethod]
    public void HandleMessageDelivery_AlreadyCompletedEntry_ShouldReturnEmpty()
    {
        var (execution, state, msgEntry) = CreateWithExecutingMessageCatch();

        // Complete it first
        execution.HandleMessageDelivery(
            "msg-catch1", msgEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Deliver again (stale callback)
        var effects = execution.HandleMessageDelivery(
            "msg-catch1", msgEntry.ActivityInstanceId, new ExpandoObject());

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    [TestMethod]
    public void HandleMessageDelivery_InterruptingBoundaryMessage_ShouldCancelAttachedAndSpawnBoundary()
    {
        // Build: start -> task (with boundary message) -> end
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var boundaryMsg = new MessageBoundaryEvent(
            "boundary-msg1", "task1", "msgDef1");
        var end = new EndEvent("end1");
        var handlerTask = new ScriptTask("handler1", "return 'handled';");

        var msgDef = new MessageDefinition("msgDef1", "OrderMessage", "= orderId");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryMsg, handlerTask],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq3", boundaryMsg, handlerTask)
            ],
            messages: [msgDef]);

        // Set correlation variable
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-123";
        state.MergeState(varsId, vars);

        // Complete start -> spawn task
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

        // Deliver boundary message — should cancel attached activity and spawn boundary
        var deliveredVars = new ExpandoObject();
        ((IDictionary<string, object?>)deliveredVars)["status"] = "received";
        var effects = execution.HandleMessageDelivery(
            "boundary-msg1", taskEntry.ActivityInstanceId, deliveredVars);

        var events = execution.GetUncommittedEvents();

        // Attached activity should be cancelled
        var cancelled = events.OfType<ActivityCancelled>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, cancelled.ActivityInstanceId);

        // Boundary event activity should be spawned
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("boundary-msg1", spawned.ActivityId);
        Assert.AreEqual("MessageBoundaryEvent", spawned.ActivityType);
    }

    // ===== HandleSignalDelivery Tests =====

    [TestMethod]
    public void HandleSignalDelivery_ActiveIntermediateCatch_ShouldCompleteActivity()
    {
        var (execution, state, sigEntry) = CreateWithExecutingSignalCatch();

        var effects = execution.HandleSignalDelivery("sig-catch1", sigEntry.ActivityInstanceId);

        // Entry should be completed
        Assert.IsTrue(sigEntry.IsCompleted);
        Assert.IsFalse(sigEntry.IsExecuting);

        // Events should include ActivityCompleted
        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(sigEntry.ActivityInstanceId, completed.ActivityInstanceId);
    }

    [TestMethod]
    public void HandleSignalDelivery_AlreadyCompletedEntry_ShouldReturnEmpty()
    {
        var (execution, state, sigEntry) = CreateWithExecutingSignalCatch();

        // Complete it first
        execution.HandleSignalDelivery("sig-catch1", sigEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Deliver again (stale callback)
        var effects = execution.HandleSignalDelivery("sig-catch1", sigEntry.ActivityInstanceId);

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    [TestMethod]
    public void HandleSignalDelivery_InterruptingBoundarySignal_ShouldCancelAttachedAndSpawnBoundary()
    {
        // Build: start -> task (with boundary signal) -> end
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var boundarySig = new SignalBoundaryEvent(
            "boundary-sig1", "task1", "sigDef1");
        var end = new EndEvent("end1");
        var handlerTask = new ScriptTask("handler1", "return 'handled';");

        var sigDef = new SignalDefinition("sigDef1", "AlertSignal");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundarySig, handlerTask],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq3", boundarySig, handlerTask)
            ],
            signals: [sigDef]);

        // Complete start -> spawn task
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

        // Deliver boundary signal — should cancel attached activity and spawn boundary
        var effects = execution.HandleSignalDelivery(
            "boundary-sig1", taskEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();

        // Attached activity should be cancelled
        var cancelled = events.OfType<ActivityCancelled>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, cancelled.ActivityInstanceId);

        // Boundary event activity should be spawned
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("boundary-sig1", spawned.ActivityId);
        Assert.AreEqual("SignalBoundaryEvent", spawned.ActivityType);
    }
}
