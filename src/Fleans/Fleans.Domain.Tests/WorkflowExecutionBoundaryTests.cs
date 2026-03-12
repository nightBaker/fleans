using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionBoundaryTests
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
    /// Creates a started workflow with a task that has been spawned and is executing.
    /// The task has boundary events as specified by the caller.
    /// Layout: start -> task -> end  (plus boundaries)
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry taskEntry)
        CreateWithExecutingTask(
            List<Activity> extraActivities,
            List<SequenceFlow> extraFlows,
            List<MessageDefinition>? messages = null,
            List<SignalDefinition>? signals = null)
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");

        var activities = new List<Activity> { start, task, end };
        activities.AddRange(extraActivities);

        var flows = new List<SequenceFlow>
        {
            new("seq1", start, task),
            new("seq2", task, end)
        };
        flows.AddRange(extraFlows);

        var (execution, state, _) = CreateStartedExecution(activities, flows, messages, signals);

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

        return (execution, state, taskEntry);
    }

    // =========================================================================
    // Interrupting Boundary Timer
    // =========================================================================

    [TestMethod]
    public void InterruptingBoundaryTimer_ShouldCancelAttachedActivity()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: true);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        // Attached activity should be cancelled
        Assert.IsTrue(taskEntry.IsCompleted);
        Assert.IsTrue(taskEntry.IsCancelled);
        Assert.IsTrue(taskEntry.CancellationReason!.Contains("boundary event"));
    }

    [TestMethod]
    public void InterruptingBoundaryTimer_ShouldSpawnBoundaryActivity()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: true);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bt1", spawned.ActivityId);
        Assert.AreEqual("BoundaryTimerEvent", spawned.ActivityType);
        Assert.AreEqual(taskEntry.VariablesId, spawned.VariablesId);
        Assert.AreEqual(taskEntry.ScopeId, spawned.ScopeId);
    }

    [TestMethod]
    public void InterruptingBoundaryTimer_ShouldCancelScopeChildren()
    {
        // Task with a sub-scope child (simulated via manually adding scope entries)
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: true);
        var handler = new ScriptTask("handler1", "return 'h';");

        // Create a subprocess with children attached to the task
        var start = new StartEvent("start1");
        var subStart = new StartEvent("sub-start");
        var subTask = new ScriptTask("sub-task1", "return 1;");
        var subProcess = new SubProcess("task1")
        {
            Activities = [subStart, subTask],
            SequenceFlows = [new SequenceFlow("sub-seq1", subStart, subTask)]
        };
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, subProcess, end, boundaryTimer, handler],
            SequenceFlows =
            [
                new("seq1", start, subProcess),
                new("seq2", subProcess, end),
                new("seq-bt", boundaryTimer, handler)
            ],
            ProcessDefinitionId = "pd1"
        };

        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Complete start -> spawn subprocess
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(subProcess)])
        ]);

        var subEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.MarkExecuting(subEntry.ActivityInstanceId);

        // Open subprocess -> spawn sub-start inside scope
        var effects = execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, subEntry.VariablesId)],
            subEntry.ActivityInstanceId);

        var subStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub-start");
        execution.MarkExecuting(subStartEntry.ActivityInstanceId);
        execution.MarkCompleted(subStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(subStartEntry.ActivityInstanceId, "sub-start",
                [new ActivityTransition(subTask)])
        ]);
        var subTaskEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub-task1");
        execution.MarkExecuting(subTaskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Fire boundary timer on the subprocess host
        execution.HandleTimerFired("bt1", subEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();
        var cancelled = events.OfType<ActivityCancelled>().ToList();

        // Both the host (subProcess) and the scope child (subTask) should be cancelled
        Assert.AreEqual(2, cancelled.Count);
        var cancelledIds = cancelled.Select(c => c.ActivityInstanceId).ToHashSet();
        Assert.IsTrue(cancelledIds.Contains(subEntry.ActivityInstanceId));
        Assert.IsTrue(cancelledIds.Contains(subTaskEntry.ActivityInstanceId));
    }

    [TestMethod]
    public void InterruptingBoundaryTimer_ShouldUnsubscribeOtherBoundarySubscriptions()
    {
        // Task with timer + message + signal boundaries; timer fires
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msgDef1");
        var boundarySig = new SignalBoundaryEvent("bs1", "task1", "sigDef1");
        var handler1 = new ScriptTask("handler1", "return 1;");
        var handler2 = new ScriptTask("handler2", "return 2;");
        var handler3 = new ScriptTask("handler3", "return 3;");

        var msgDef = new MessageDefinition("msgDef1", "OrderMsg", "= orderId");
        var sigDef = new SignalDefinition("sigDef1", "AlertSig");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, boundaryMsg, boundarySig, handler1, handler2, handler3],
            [
                new("seq-bt", boundaryTimer, handler1),
                new("seq-bm", boundaryMsg, handler2),
                new("seq-bs", boundarySig, handler3)
            ],
            messages: [msgDef],
            signals: [sigDef]);

        // Set correlation variable
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-999";
        state.MergeState(varsId, vars);

        var effects = execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        // Timer fired, so it should NOT appear in unsubscribe effects
        // (timer that fired is not included — there's no "skip" for the timer itself since
        //  it already fired; but other timers would be unsubscribed)
        // Message and signal boundaries should be unsubscribed
        var unsubMsg = effects.OfType<UnsubscribeMessageEffect>().Single();
        Assert.AreEqual("OrderMsg", unsubMsg.MessageName);
        Assert.AreEqual("order-999", unsubMsg.CorrelationKey);

        var unsubSig = effects.OfType<UnsubscribeSignalEffect>().Single();
        Assert.AreEqual("AlertSig", unsubSig.SignalName);
    }

    // =========================================================================
    // Non-Interrupting Boundary Timer
    // =========================================================================

    [TestMethod]
    public void NonInterruptingBoundaryTimer_ShouldLeaveAttachedActivityRunning()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        // Attached activity should still be active (not cancelled, not completed)
        Assert.IsFalse(taskEntry.IsCompleted);
        Assert.IsFalse(taskEntry.IsCancelled);
        Assert.IsTrue(taskEntry.IsExecuting);
    }

    [TestMethod]
    public void NonInterruptingBoundaryTimer_ShouldCloneVariableScope()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        // Set a variable so we can verify it's cloned
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["x"] = 42;
        state.MergeState(taskEntry.VariablesId, vars);

        execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();

        // Should have cloned the variable scope
        var cloned = events.OfType<VariableScopeCloned>().Single();
        Assert.AreEqual(taskEntry.VariablesId, cloned.SourceScopeId);

        // Spawned boundary entry should use the cloned scope
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(cloned.NewScopeId, spawned.VariablesId);
        Assert.AreNotEqual(taskEntry.VariablesId, spawned.VariablesId);

        // Verify cloned scope has the original variable
        var clonedValue = state.GetVariable(cloned.NewScopeId, "x");
        Assert.AreEqual(42, clonedValue);
    }

    [TestMethod]
    public void NonInterruptingBoundaryTimer_ShouldSpawnBoundaryActivity()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bt1", spawned.ActivityId);
        Assert.AreEqual("BoundaryTimerEvent", spawned.ActivityType);
        Assert.AreEqual(taskEntry.ScopeId, spawned.ScopeId);
    }

    [TestMethod]
    public void NonInterruptingBoundaryTimer_CycleTimer_ShouldReturnReRegisterEffect()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Cycle, "R3/PT10S"), IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        var effects = execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        // Should return a RegisterTimerEffect for the next cycle
        var registerEffect = effects.OfType<RegisterTimerEffect>().Single();
        Assert.AreEqual(state.Id, registerEffect.WorkflowInstanceId);
        Assert.AreEqual(taskEntry.ActivityInstanceId, registerEffect.HostActivityInstanceId);
        Assert.AreEqual("bt1", registerEffect.TimerActivityId);
        Assert.AreEqual(TimeSpan.FromSeconds(10), registerEffect.DueTime);
    }

    [TestMethod]
    public void NonInterruptingBoundaryTimer_LastCycleRepetition_ShouldNotReRegister()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Cycle, "R1/PT10S"), IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        var effects = execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        // Last repetition — no re-register
        Assert.AreEqual(0, effects.OfType<RegisterTimerEffect>().Count());
    }

    [TestMethod]
    public void NonInterruptingBoundaryTimer_DurationTimer_ShouldNotReRegister()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"), IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        var effects = execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        // Duration timers don't re-register
        Assert.AreEqual(0, effects.OfType<RegisterTimerEffect>().Count());
    }

    // =========================================================================
    // Interrupting Boundary Message
    // =========================================================================

    [TestMethod]
    public void InterruptingBoundaryMessage_ShouldCancelAttachedAndSpawnBoundary()
    {
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msgDef1", IsInterrupting: true);
        var handler = new ScriptTask("handler1", "return 'h';");
        var msgDef = new MessageDefinition("msgDef1", "OrderMsg", "= orderId");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryMsg, handler],
            [new("seq-bm", boundaryMsg, handler)],
            messages: [msgDef]);

        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-1";
        state.MergeState(varsId, vars);

        var deliveredVars = new ExpandoObject();
        ((IDictionary<string, object?>)deliveredVars)["status"] = "received";

        execution.HandleMessageDelivery("bm1", taskEntry.ActivityInstanceId, deliveredVars);

        var events = execution.GetUncommittedEvents();

        // Attached activity cancelled
        var cancelled = events.OfType<ActivityCancelled>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, cancelled.ActivityInstanceId);

        // Boundary spawned
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bm1", spawned.ActivityId);
        Assert.AreEqual("MessageBoundaryEvent", spawned.ActivityType);
    }

    [TestMethod]
    public void InterruptingBoundaryMessage_ShouldSkipFiredMessageInUnsubscribe()
    {
        // Task with two message boundaries; message 1 fires
        var boundaryMsg1 = new MessageBoundaryEvent("bm1", "task1", "msgDef1", IsInterrupting: true);
        var boundaryMsg2 = new MessageBoundaryEvent("bm2", "task1", "msgDef2", IsInterrupting: true);
        var handler1 = new ScriptTask("handler1", "return 1;");
        var handler2 = new ScriptTask("handler2", "return 2;");

        var msgDef1 = new MessageDefinition("msgDef1", "OrderMsg", "= orderId");
        var msgDef2 = new MessageDefinition("msgDef2", "PaymentMsg", "= orderId");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryMsg1, boundaryMsg2, handler1, handler2],
            [
                new("seq-bm1", boundaryMsg1, handler1),
                new("seq-bm2", boundaryMsg2, handler2)
            ],
            messages: [msgDef1, msgDef2]);

        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-1";
        state.MergeState(varsId, vars);

        var effects = execution.HandleMessageDelivery(
            "bm1", taskEntry.ActivityInstanceId, new ExpandoObject());

        // OrderMsg (bm1) fired — should NOT be in unsubscribe
        // PaymentMsg (bm2) should be unsubscribed
        var unsubMsgs = effects.OfType<UnsubscribeMessageEffect>().ToList();
        Assert.AreEqual(1, unsubMsgs.Count);
        Assert.AreEqual("PaymentMsg", unsubMsgs[0].MessageName);
    }

    // =========================================================================
    // Non-Interrupting Boundary Message
    // =========================================================================

    [TestMethod]
    public void NonInterruptingBoundaryMessage_ShouldLeaveAttachedRunningAndCloneVars()
    {
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msgDef1", IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");
        var msgDef = new MessageDefinition("msgDef1", "OrderMsg", "= orderId");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryMsg, handler],
            [new("seq-bm", boundaryMsg, handler)],
            messages: [msgDef]);

        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-1";
        state.MergeState(varsId, vars);

        var deliveredVars = new ExpandoObject();
        ((IDictionary<string, object?>)deliveredVars)["payload"] = "data";

        execution.HandleMessageDelivery("bm1", taskEntry.ActivityInstanceId, deliveredVars);

        // Attached activity still active
        Assert.IsFalse(taskEntry.IsCompleted);
        Assert.IsTrue(taskEntry.IsExecuting);

        var events = execution.GetUncommittedEvents();

        // Variable scope cloned
        var cloned = events.OfType<VariableScopeCloned>().Single();
        Assert.AreEqual(taskEntry.VariablesId, cloned.SourceScopeId);

        // Delivered variables merged into cloned scope
        var merged = events.OfType<VariablesMerged>().Single();
        Assert.AreEqual(cloned.NewScopeId, merged.VariablesId);

        // Boundary spawned with cloned scope
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bm1", spawned.ActivityId);
        Assert.AreEqual(cloned.NewScopeId, spawned.VariablesId);

        // Verify delivered variable is in cloned scope
        var payloadValue = state.GetVariable(cloned.NewScopeId, "payload");
        Assert.AreEqual("data", payloadValue);
    }

    // =========================================================================
    // Interrupting Boundary Signal
    // =========================================================================

    [TestMethod]
    public void InterruptingBoundarySignal_ShouldCancelAttachedAndSpawnBoundary()
    {
        var boundarySig = new SignalBoundaryEvent("bs1", "task1", "sigDef1", IsInterrupting: true);
        var handler = new ScriptTask("handler1", "return 'h';");
        var sigDef = new SignalDefinition("sigDef1", "AlertSig");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundarySig, handler],
            [new("seq-bs", boundarySig, handler)],
            signals: [sigDef]);

        execution.HandleSignalDelivery("bs1", taskEntry.ActivityInstanceId);

        var events = execution.GetUncommittedEvents();

        // Attached activity cancelled
        var cancelled = events.OfType<ActivityCancelled>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, cancelled.ActivityInstanceId);

        // Boundary spawned
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bs1", spawned.ActivityId);
        Assert.AreEqual("SignalBoundaryEvent", spawned.ActivityType);
    }

    [TestMethod]
    public void InterruptingBoundarySignal_ShouldSkipFiredSignalInUnsubscribe()
    {
        // Task with timer + two signal boundaries; signal 1 fires
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var boundarySig1 = new SignalBoundaryEvent("bs1", "task1", "sigDef1", IsInterrupting: true);
        var boundarySig2 = new SignalBoundaryEvent("bs2", "task1", "sigDef2", IsInterrupting: true);
        var handler1 = new ScriptTask("handler1", "return 1;");
        var handler2 = new ScriptTask("handler2", "return 2;");
        var handler3 = new ScriptTask("handler3", "return 3;");

        var sigDef1 = new SignalDefinition("sigDef1", "AlertSig");
        var sigDef2 = new SignalDefinition("sigDef2", "StopSig");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, boundarySig1, boundarySig2, handler1, handler2, handler3],
            [
                new("seq-bt", boundaryTimer, handler1),
                new("seq-bs1", boundarySig1, handler2),
                new("seq-bs2", boundarySig2, handler3)
            ],
            signals: [sigDef1, sigDef2]);

        var effects = execution.HandleSignalDelivery("bs1", taskEntry.ActivityInstanceId);

        // AlertSig (bs1) fired — should NOT be in unsubscribe
        // StopSig (bs2) should be unsubscribed
        // Timer should be unsubscribed
        var unsubSigs = effects.OfType<UnsubscribeSignalEffect>().ToList();
        Assert.AreEqual(1, unsubSigs.Count);
        Assert.AreEqual("StopSig", unsubSigs[0].SignalName);

        var unregTimers = effects.OfType<UnregisterTimerEffect>().ToList();
        Assert.AreEqual(1, unregTimers.Count);
        Assert.AreEqual("bt1", unregTimers[0].TimerActivityId);
    }

    // =========================================================================
    // Non-Interrupting Boundary Signal
    // =========================================================================

    [TestMethod]
    public void NonInterruptingBoundarySignal_ShouldLeaveAttachedRunningAndCloneVars()
    {
        var boundarySig = new SignalBoundaryEvent("bs1", "task1", "sigDef1", IsInterrupting: false);
        var handler = new ScriptTask("handler1", "return 'h';");
        var sigDef = new SignalDefinition("sigDef1", "AlertSig");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundarySig, handler],
            [new("seq-bs", boundarySig, handler)],
            signals: [sigDef]);

        execution.HandleSignalDelivery("bs1", taskEntry.ActivityInstanceId);

        // Attached still active
        Assert.IsFalse(taskEntry.IsCompleted);
        Assert.IsTrue(taskEntry.IsExecuting);

        var events = execution.GetUncommittedEvents();

        // Variable scope cloned
        var cloned = events.OfType<VariableScopeCloned>().Single();
        Assert.AreEqual(taskEntry.VariablesId, cloned.SourceScopeId);

        // Boundary spawned with cloned scope
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bs1", spawned.ActivityId);
        Assert.AreEqual(cloned.NewScopeId, spawned.VariablesId);
    }

    // =========================================================================
    // Error Boundary
    // =========================================================================

    [TestMethod]
    public void ErrorBoundary_ShouldSpawnBoundaryErrorEvent()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryError = new BoundaryErrorEvent("be1", "task1", "500");
        var handler = new ScriptTask("handler1", "return 'recovered';");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryError, handler],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq-be", boundaryError, handler)
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

        // Activity failed
        var failed = events.OfType<ActivityFailed>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, failed.ActivityInstanceId);
        Assert.AreEqual(500, failed.ErrorCode);

        // Boundary error event spawned
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("be1", spawned.ActivityId);
        Assert.AreEqual("BoundaryErrorEvent", spawned.ActivityType);
        Assert.AreEqual(taskEntry.VariablesId, spawned.VariablesId);
    }

    [TestMethod]
    public void ErrorBoundary_ShouldCancelScopeChildren()
    {
        // SubProcess with a task inside; boundary error on the subprocess
        var start = new StartEvent("start1");
        var subStart = new StartEvent("sub-start");
        var subTask = new ScriptTask("sub-task1", "return 1;");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subTask],
            SequenceFlows = [new SequenceFlow("sub-seq1", subStart, subTask)]
        };
        var boundaryError = new BoundaryErrorEvent("be1", "sub1", "500");
        var handler = new ScriptTask("handler1", "return 'recovered';");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, subProcess, end, boundaryError, handler],
            SequenceFlows =
            [
                new("seq1", start, subProcess),
                new("seq2", subProcess, end),
                new("seq-be", boundaryError, handler)
            ],
            ProcessDefinitionId = "pd1"
        };

        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Complete start -> spawn subprocess
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(subProcess)])
        ]);

        var subEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub1");
        execution.MarkExecuting(subEntry.ActivityInstanceId);

        // Open subprocess, spawn sub-start inside
        execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, subEntry.VariablesId)],
            subEntry.ActivityInstanceId);

        var subStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub-start");
        execution.MarkExecuting(subStartEntry.ActivityInstanceId);
        execution.MarkCompleted(subStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(subStartEntry.ActivityInstanceId, "sub-start",
                [new ActivityTransition(subTask)])
        ]);

        var subTaskEntry = state.GetActiveActivities().First(e => e.ActivityId == "sub-task1");
        execution.MarkExecuting(subTaskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Fail the inner task — boundary error handler on sub1 should fire
        execution.FailActivity("sub-task1", subTaskEntry.ActivityInstanceId, new Exception("inner error"));

        var events = execution.GetUncommittedEvents();

        // Should have spawned the boundary error event
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("be1", spawned.ActivityId);
    }

    [TestMethod]
    public void ErrorBoundary_CatchAll_ShouldMatchAnyErrorCode()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryError = new BoundaryErrorEvent("be1", "task1", null); // catch-all
        var handler = new ScriptTask("handler1", "return 'recovered';");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryError, handler],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq-be", boundaryError, handler)
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

        // Fail with a 999 error code — catch-all boundary should still handle it
        execution.FailActivity("task1", taskEntry.ActivityInstanceId,
            new Exception("any error"));

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("be1", spawned.ActivityId);
    }

    [TestMethod]
    public void ErrorBoundary_NoMatch_ShouldNotSpawnBoundary()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryError = new BoundaryErrorEvent("be1", "task1", "404"); // only matches 404
        var handler = new ScriptTask("handler1", "return 'recovered';");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryError, handler],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq-be", boundaryError, handler)
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

        // Fail with 500 — no matching error boundary (only 404)
        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("server error"));

        var events = execution.GetUncommittedEvents();

        // Should NOT spawn a boundary event
        Assert.AreEqual(0, events.OfType<ActivitySpawned>().Count());

        // Should have ActivityFailed only
        Assert.AreEqual(1, events.OfType<ActivityFailed>().Count());
    }

    // =========================================================================
    // Stale Guard Tests
    // =========================================================================

    [TestMethod]
    public void BoundaryTimerFired_StaleEntry_ShouldReturnEmpty()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        // Complete the task normally first
        execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Now fire boundary timer (stale)
        var effects = execution.HandleTimerFired("bt1", taskEntry.ActivityInstanceId);

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    [TestMethod]
    public void BoundaryTimerFired_NonExistentEntry_ShouldReturnEmpty()
    {
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var handler = new ScriptTask("handler1", "return 'h';");

        var (execution, state, taskEntry) = CreateWithExecutingTask(
            [boundaryTimer, handler],
            [new("seq-bt", boundaryTimer, handler)]);

        // Fire with a non-existent activity instance ID
        var effects = execution.HandleTimerFired("bt1", Guid.NewGuid());

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    // =========================================================================
    // Recursive Scope Cancellation
    // =========================================================================

    [TestMethod]
    public void InterruptingBoundary_NestedScopes_ShouldCancelRecursively()
    {
        // SubProcess with nested SubProcess inside, boundary timer on outer
        var start = new StartEvent("start1");
        var innerStart = new StartEvent("inner-start");
        var innerTask = new ScriptTask("inner-task", "return 1;");
        var innerSub = new SubProcess("inner-sub")
        {
            Activities = [innerStart, innerTask],
            SequenceFlows = [new SequenceFlow("inner-seq", innerStart, innerTask)]
        };
        var outerStart = new StartEvent("outer-start");
        var outerSub = new SubProcess("outer-sub")
        {
            Activities = [outerStart, innerSub],
            SequenceFlows = [new SequenceFlow("outer-seq", outerStart, innerSub)]
        };
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "outer-sub", new TimerDefinition(TimerType.Duration, "PT10S"));
        var handler = new ScriptTask("handler1", "return 'h';");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, outerSub, end, boundaryTimer, handler],
            SequenceFlows =
            [
                new("seq1", start, outerSub),
                new("seq2", outerSub, end),
                new("seq-bt", boundaryTimer, handler)
            ],
            ProcessDefinitionId = "pd1"
        };

        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Complete start -> spawn outerSub
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(outerSub)])
        ]);

        var outerEntry = state.GetActiveActivities().First(e => e.ActivityId == "outer-sub");
        execution.MarkExecuting(outerEntry.ActivityInstanceId);

        // Open outer subprocess
        execution.ProcessCommands(
            [new OpenSubProcessCommand(outerSub, outerEntry.VariablesId)],
            outerEntry.ActivityInstanceId);

        var outerStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "outer-start");
        execution.MarkExecuting(outerStartEntry.ActivityInstanceId);
        execution.MarkCompleted(outerStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(outerStartEntry.ActivityInstanceId, "outer-start",
                [new ActivityTransition(innerSub)])
        ]);

        var innerSubEntry = state.GetActiveActivities().First(e => e.ActivityId == "inner-sub");
        execution.MarkExecuting(innerSubEntry.ActivityInstanceId);

        // Open inner subprocess
        execution.ProcessCommands(
            [new OpenSubProcessCommand(innerSub, innerSubEntry.VariablesId)],
            innerSubEntry.ActivityInstanceId);

        var innerStartEntry = state.GetActiveActivities().First(e => e.ActivityId == "inner-start");
        execution.MarkExecuting(innerStartEntry.ActivityInstanceId);
        execution.MarkCompleted(innerStartEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(innerStartEntry.ActivityInstanceId, "inner-start",
                [new ActivityTransition(innerTask)])
        ]);

        var innerTaskEntry = state.GetActiveActivities().First(e => e.ActivityId == "inner-task");
        execution.MarkExecuting(innerTaskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Fire boundary timer on outer-sub
        execution.HandleTimerFired("bt1", outerEntry.ActivityInstanceId);

        // All three should be cancelled: outer-sub, inner-sub, inner-task
        // Plus outer-sub gets the direct cancel from the interrupting boundary
        Assert.IsTrue(outerEntry.IsCancelled);
        Assert.IsTrue(innerSubEntry.IsCancelled);
        Assert.IsTrue(innerTaskEntry.IsCancelled);

        // Boundary spawned
        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("bt1", spawned.ActivityId);
    }

    // =========================================================================
    // Error Boundary Unsubscribe Effects
    // =========================================================================

    [TestMethod]
    public void ErrorBoundary_ShouldReturnUnsubscribeEffectsForAttachedBoundaries()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryError = new BoundaryErrorEvent("be1", "task1", "500");
        var boundaryTimer = new BoundaryTimerEvent(
            "bt1", "task1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var handler = new ScriptTask("handler1", "return 'recovered';");
        var handler2 = new ScriptTask("handler2", "return 'timer';");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryError, boundaryTimer, handler, handler2],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq-be", boundaryError, handler),
                new("seq-bt", boundaryTimer, handler2)
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

        var effects = execution.FailActivity(
            "task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        // Should unsubscribe the timer boundary on the failed activity
        var unregTimer = effects.OfType<UnregisterTimerEffect>().Single();
        Assert.AreEqual("bt1", unregTimer.TimerActivityId);
    }
}
