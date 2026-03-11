using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionActivityLifecycleTests
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
    /// Creates a started workflow with a single script task that has been spawned
    /// and is executing (ready to be completed or failed).
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry taskEntry)
        CreateWithExecutingTask(
            List<Activity>? extraActivities = null,
            List<SequenceFlow>? extraFlows = null,
            List<MessageDefinition>? messages = null,
            List<SignalDefinition>? signals = null)
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");

        var activities = new List<Activity> { start, task, end };
        if (extraActivities is not null)
            activities.AddRange(extraActivities);

        var flows = new List<SequenceFlow>
        {
            new("seq1", start, task),
            new("seq2", task, end)
        };
        if (extraFlows is not null)
            flows.AddRange(extraFlows);

        var (execution, state, _) = CreateStartedExecution(activities, flows, messages, signals);

        // Complete the start event to spawn the task
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

    // ===== CompleteActivity Tests =====

    [TestMethod]
    public void CompleteActivity_ShouldCompleteEntryAndMergeVariables()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        var variables = new ExpandoObject();
        ((IDictionary<string, object?>)variables)["result"] = 42;

        var effects = execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, variables);

        // Entry should be completed
        Assert.IsTrue(taskEntry.IsCompleted);
        Assert.IsFalse(taskEntry.IsExecuting);

        // Variables should be merged
        var mergedVars = state.GetMergedVariables(taskEntry.VariablesId);
        var dict = (IDictionary<string, object?>)mergedVars;
        Assert.AreEqual(42, dict["result"]);

        // Events should include ActivityCompleted
        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, completed.ActivityInstanceId);
    }

    [TestMethod]
    public void CompleteActivity_ByActivityIdOnly_ShouldResolveFirstActiveEntry()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        var effects = execution.CompleteActivity("task1", null, new ExpandoObject());

        // Entry should be completed
        Assert.IsTrue(taskEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteActivity_AlreadyCompleted_ShouldReturnEmpty()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        // Complete it once
        execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Complete again (stale callback)
        var effects = execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    [TestMethod]
    public void CompleteActivity_WithBoundaryTimerEvent_ShouldReturnUnregisterTimerEffect()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryTimer = new BoundaryTimerEvent(
            "boundary-timer1", "task1",
            new TimerDefinition(TimerType.Duration, "PT10S"));

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryTimer],
            [new("seq1", start, task), new("seq2", task, end)]);

        // Spawn and execute the task
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

        var effects = execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());

        var timerEffect = effects.OfType<UnregisterTimerEffect>().Single();
        Assert.AreEqual(state.Id, timerEffect.WorkflowInstanceId);
        Assert.AreEqual(taskEntry.ActivityInstanceId, timerEffect.HostActivityInstanceId);
        Assert.AreEqual("boundary-timer1", timerEffect.TimerActivityId);
    }

    [TestMethod]
    public void CompleteActivity_WithBoundaryMessageEvent_ShouldReturnUnsubscribeMessageEffect()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryMsg = new MessageBoundaryEvent(
            "boundary-msg1", "task1", "msgDef1");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var msgDef = new MessageDefinition("msgDef1", "OrderMessage", "= orderId");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryMsg],
            [new("seq1", start, task), new("seq2", task, end)],
            messages: [msgDef]);

        // Set up a variable for correlation key resolution
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-123";
        state.MergeState(varsId, vars);

        // Spawn and execute the task
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

        var effects = execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());

        var msgEffect = effects.OfType<UnsubscribeMessageEffect>().Single();
        Assert.AreEqual("OrderMessage", msgEffect.MessageName);
        Assert.AreEqual("order-123", msgEffect.CorrelationKey);
    }

    [TestMethod]
    public void CompleteActivity_WithBoundarySignalEvent_ShouldReturnUnsubscribeSignalEffect()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundarySig = new SignalBoundaryEvent(
            "boundary-sig1", "task1", "sigDef1");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var sigDef = new SignalDefinition("sigDef1", "AlertSignal");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundarySig],
            [new("seq1", start, task), new("seq2", task, end)],
            signals: [sigDef]);

        // Spawn and execute the task
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

        var effects = execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());

        var sigEffect = effects.OfType<UnsubscribeSignalEffect>().Single();
        Assert.AreEqual("AlertSignal", sigEffect.SignalName);
    }

    // ===== FailActivity Tests =====

    [TestMethod]
    public void FailActivity_GenericException_ShouldSetErrorCode500()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        var effects = execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        // Entry should be failed
        Assert.IsTrue(taskEntry.IsCompleted);
        Assert.AreEqual(500, taskEntry.ErrorCode);
        Assert.AreEqual("boom", taskEntry.ErrorMessage);

        // Events should include ActivityFailed
        var events = execution.GetUncommittedEvents();
        var failed = events.OfType<ActivityFailed>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, failed.ActivityInstanceId);
        Assert.AreEqual(500, failed.ErrorCode);
        Assert.AreEqual("boom", failed.ErrorMessage);
    }

    [TestMethod]
    public void FailActivity_ActivityException_ShouldSetErrorCode400()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        var effects = execution.FailActivity(
            "task1", taskEntry.ActivityInstanceId,
            new BadRequestActivityException("invalid input"));

        Assert.IsTrue(taskEntry.IsCompleted);
        Assert.AreEqual(400, taskEntry.ErrorCode);
        Assert.AreEqual("invalid input", taskEntry.ErrorMessage);

        var events = execution.GetUncommittedEvents();
        var failed = events.OfType<ActivityFailed>().Single();
        Assert.AreEqual(400, failed.ErrorCode);
    }

    [TestMethod]
    public void FailActivity_AlreadyCompleted_ShouldReturnEmpty()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        // Complete it first
        execution.CompleteActivity("task1", taskEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Fail it (stale callback)
        var effects = execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("late"));

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    [TestMethod]
    public void FailActivity_ByActivityIdOnly_ShouldResolveFirstActiveEntry()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        var effects = execution.FailActivity("task1", null, new Exception("fail"));

        Assert.IsTrue(taskEntry.IsCompleted);
        Assert.AreEqual(500, taskEntry.ErrorCode);
    }

    [TestMethod]
    public void FailActivity_WithBoundaryErrorHandler_ShouldSpawnBoundaryErrorEvent()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryError = new BoundaryErrorEvent("boundary-error1", "task1", "500");
        var errorHandler = new ScriptTask("errorHandler1", "return 'handled';");

        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryError, errorHandler],
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

        var effects = execution.FailActivity(
            "task1", taskEntry.ActivityInstanceId, new Exception("something broke"));

        // Should emit ActivityFailed + ActivitySpawned for boundary error event
        var events = execution.GetUncommittedEvents();
        var failed = events.OfType<ActivityFailed>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, failed.ActivityInstanceId);

        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("boundary-error1", spawned.ActivityId);
        Assert.AreEqual("BoundaryErrorEvent", spawned.ActivityType);
        Assert.AreEqual(taskEntry.VariablesId, spawned.VariablesId);
    }

    // ===== CancelEventBasedGatewaySiblings Tests =====

    [TestMethod]
    public void CompleteActivity_EventBasedGateway_ShouldCancelSiblingsAndReturnUnsubscribeEffects()
    {
        // Build: start -> ebgw -> [timerCatch, msgCatch, sigCatch]
        var start = new StartEvent("start1");
        var ebgw = new EventBasedGateway("ebgw1");
        var timerCatch = new TimerIntermediateCatchEvent(
            "timer-catch1", new TimerDefinition(TimerType.Duration, "PT30S"));
        var msgCatch = new MessageIntermediateCatchEvent("msg-catch1", "msgDef1");
        var sigCatch = new SignalIntermediateCatchEvent("sig-catch1", "sigDef1");
        var end = new EndEvent("end1");

        var msgDef = new MessageDefinition("msgDef1", "OrderMsg", "= orderId");
        var sigDef = new SignalDefinition("sigDef1", "AlertSig");

        var (execution, state, _) = CreateStartedExecution(
            [start, ebgw, timerCatch, msgCatch, sigCatch, end],
            [
                new("seq1", start, ebgw),
                new("seq2", ebgw, timerCatch),
                new("seq3", ebgw, msgCatch),
                new("seq4", ebgw, sigCatch),
                new("seq5", timerCatch, end),
                new("seq6", msgCatch, end),
                new("seq7", sigCatch, end)
            ],
            messages: [msgDef],
            signals: [sigDef]);

        // Set up variable for message correlation
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-456";
        state.MergeState(varsId, vars);

        // Complete start -> spawn ebgw
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(ebgw)])
        ]);

        // Complete ebgw -> spawn all three catch events
        var ebgwEntry = state.GetActiveActivities().First(e => e.ActivityId == "ebgw1");
        execution.MarkExecuting(ebgwEntry.ActivityInstanceId);
        execution.MarkCompleted(ebgwEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(ebgwEntry.ActivityInstanceId, "ebgw1",
            [
                new ActivityTransition(timerCatch),
                new ActivityTransition(msgCatch),
                new ActivityTransition(sigCatch)
            ])
        ]);

        // All three catch events should be active
        var timerEntry = state.GetActiveActivities().First(e => e.ActivityId == "timer-catch1");
        var msgEntry = state.GetActiveActivities().First(e => e.ActivityId == "msg-catch1");
        var sigEntry = state.GetActiveActivities().First(e => e.ActivityId == "sig-catch1");

        // Mark them all as executing
        execution.MarkExecuting(timerEntry.ActivityInstanceId);
        execution.MarkExecuting(msgEntry.ActivityInstanceId);
        execution.MarkExecuting(sigEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Now complete the timer catch - this should cancel msg and sig siblings
        var effects = execution.CompleteActivity(
            "timer-catch1", timerEntry.ActivityInstanceId, new ExpandoObject());

        // Verify siblings are cancelled
        var events = execution.GetUncommittedEvents();
        var cancelled = events.OfType<ActivityCancelled>().ToList();
        Assert.AreEqual(2, cancelled.Count);

        var cancelledIds = cancelled.Select(c => c.ActivityInstanceId).ToHashSet();
        Assert.IsTrue(cancelledIds.Contains(msgEntry.ActivityInstanceId));
        Assert.IsTrue(cancelledIds.Contains(sigEntry.ActivityInstanceId));

        // Verify unsubscribe effects for siblings
        var unregisterTimer = effects.OfType<UnregisterTimerEffect>().ToList();
        // Timer was the completed one, not a sibling - no UnregisterTimerEffect for siblings
        Assert.AreEqual(0, unregisterTimer.Count);

        var unsubMsg = effects.OfType<UnsubscribeMessageEffect>().Single();
        Assert.AreEqual("OrderMsg", unsubMsg.MessageName);
        Assert.AreEqual("order-456", unsubMsg.CorrelationKey);

        var unsubSig = effects.OfType<UnsubscribeSignalEffect>().Single();
        Assert.AreEqual("AlertSig", unsubSig.SignalName);
    }

    // ===== Apply Stubs Tests =====

    [TestMethod]
    public void Apply_ActivityFailed_ShouldSetEntryErrorState()
    {
        var (execution, state, taskEntry) = CreateWithExecutingTask();

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("test error"));

        Assert.IsTrue(taskEntry.IsCompleted);
        Assert.AreEqual(500, taskEntry.ErrorCode);
        Assert.AreEqual("test error", taskEntry.ErrorMessage);
        Assert.IsFalse(taskEntry.IsExecuting);
    }

    [TestMethod]
    public void Apply_ActivityCancelled_ShouldSetEntryCancelled()
    {
        // Set up event-based gateway scenario to trigger cancellation
        var start = new StartEvent("start1");
        var ebgw = new EventBasedGateway("ebgw1");
        var timerCatch = new TimerIntermediateCatchEvent(
            "timer-catch1", new TimerDefinition(TimerType.Duration, "PT30S"));
        var sigCatch = new SignalIntermediateCatchEvent("sig-catch1", "sigDef1");
        var end = new EndEvent("end1");

        var sigDef = new SignalDefinition("sigDef1", "TestSig");

        var (execution, state, _) = CreateStartedExecution(
            [start, ebgw, timerCatch, sigCatch, end],
            [
                new("seq1", start, ebgw),
                new("seq2", ebgw, timerCatch),
                new("seq3", ebgw, sigCatch),
                new("seq4", timerCatch, end),
                new("seq5", sigCatch, end)
            ],
            signals: [sigDef]);

        // Complete start -> ebgw -> spawn catch events
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(ebgw)])
        ]);

        var ebgwEntry = state.GetActiveActivities().First(e => e.ActivityId == "ebgw1");
        execution.MarkExecuting(ebgwEntry.ActivityInstanceId);
        execution.MarkCompleted(ebgwEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(ebgwEntry.ActivityInstanceId, "ebgw1",
            [
                new ActivityTransition(timerCatch),
                new ActivityTransition(sigCatch)
            ])
        ]);

        var timerEntry = state.GetActiveActivities().First(e => e.ActivityId == "timer-catch1");
        var sigEntry = state.GetActiveActivities().First(e => e.ActivityId == "sig-catch1");
        execution.MarkExecuting(timerEntry.ActivityInstanceId);
        execution.MarkExecuting(sigEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Complete timer → should cancel sig sibling
        execution.CompleteActivity("timer-catch1", timerEntry.ActivityInstanceId, new ExpandoObject());

        // sigEntry should be cancelled
        Assert.IsTrue(sigEntry.IsCompleted);
        Assert.IsTrue(sigEntry.IsCancelled);
        Assert.IsNotNull(sigEntry.CancellationReason);
        Assert.IsTrue(sigEntry.CancellationReason!.Contains("Event-based gateway"));
    }

    // ===== Fail overload on ActivityInstanceEntry =====

    [TestMethod]
    public void ActivityInstanceEntry_FailWithCodeAndMessage_ShouldSetFields()
    {
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "act1", Guid.NewGuid());
        entry.SetActivityType("ScriptTask");
        entry.SetVariablesId(Guid.NewGuid());
        entry.Execute();

        entry.Fail(500, "something broke");

        Assert.IsTrue(entry.IsCompleted);
        Assert.IsFalse(entry.IsExecuting);
        Assert.AreEqual(500, entry.ErrorCode);
        Assert.AreEqual("something broke", entry.ErrorMessage);
        Assert.IsNotNull(entry.CompletedAt);
    }

    [TestMethod]
    public void ActivityInstanceEntry_FailWithCodeAndMessage_AlreadyCompleted_ShouldThrow()
    {
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "act1", Guid.NewGuid());
        entry.SetActivityType("ScriptTask");
        entry.SetVariablesId(Guid.NewGuid());
        entry.Execute();
        entry.Complete();

        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Fail(500, "too late"));
    }

    // ===== FailActivity with no boundary handler and child workflow =====

    [TestMethod]
    public void FailActivity_ChildWorkflow_NoActiveActivities_ShouldReturnNotifyParentFailedEffect()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, task],
            SequenceFlows = [new SequenceFlow("seq1", start, task)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Set parent info (this is a child workflow)
        var parentId = Guid.NewGuid();
        state.SetParentInfo(parentId, "callActivity1");

        // Complete start, spawn task
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

        // Fail the only active task - no more active activities
        var exception = new Exception("child failed");
        var effects = execution.FailActivity("task1", taskEntry.ActivityInstanceId, exception);

        // Should notify parent of failure
        var notifyEffect = effects.OfType<NotifyParentFailedEffect>().Single();
        Assert.AreEqual(parentId, notifyEffect.ParentInstanceId);
        Assert.AreEqual("callActivity1", notifyEffect.ParentActivityId);
        Assert.AreEqual(exception, notifyEffect.Exception);
    }
}
