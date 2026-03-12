using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionConditionAndChildTests
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
    /// Creates a workflow with an exclusive gateway that has two conditional sequence flows
    /// and optionally a default flow. Returns the execution and the gateway entry in executing state.
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry gatewayEntry)
        CreateWithExclusiveGateway(bool withDefaultFlow = false)
    {
        var start = new StartEvent("start1");
        var gateway = new ExclusiveGateway("xgw1");
        var taskA = new ScriptTask("taskA", "return 'A';");
        var taskB = new ScriptTask("taskB", "return 'B';");
        var taskDefault = new ScriptTask("taskDefault", "return 'default';");
        var end = new EndEvent("end1");

        var activities = new List<Activity> { start, gateway, taskA, taskB, taskDefault, end };

        var flows = new List<SequenceFlow>
        {
            new("seq1", start, gateway),
            new ConditionalSequenceFlow("condA", gateway, taskA, "x > 10"),
            new ConditionalSequenceFlow("condB", gateway, taskB, "x > 5"),
            new("seqA-end", taskA, end),
            new("seqB-end", taskB, end),
            new("seqDefault-end", taskDefault, end)
        };

        if (withDefaultFlow)
            flows.Add(new DefaultSequenceFlow("defaultFlow", gateway, taskDefault));

        var (execution, state, _) = CreateStartedExecution(activities, flows);

        // Complete start -> spawn gateway
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(gateway)])
        ]);

        // Execute gateway (add conditions via ProcessCommands)
        var gatewayEntry = state.GetActiveActivities().First(e => e.ActivityId == "xgw1");
        execution.MarkExecuting(gatewayEntry.ActivityInstanceId);

        var commands = new List<IExecutionCommand>
        {
            new AddConditionsCommand(
                ["condA", "condB"],
                [
                    new ConditionEvaluation("condA", "x > 10"),
                    new ConditionEvaluation("condB", "x > 5")
                ])
        };
        execution.ProcessCommands(commands, gatewayEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        return (execution, state, gatewayEntry);
    }

    /// <summary>
    /// Creates a workflow with an inclusive gateway (fork) that has two conditional sequence flows
    /// and optionally a default flow.
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry gatewayEntry)
        CreateWithInclusiveGateway(bool withDefaultFlow = false)
    {
        var start = new StartEvent("start1");
        var gateway = new InclusiveGateway("igw1", IsFork: true);
        var taskA = new ScriptTask("taskA", "return 'A';");
        var taskB = new ScriptTask("taskB", "return 'B';");
        var taskDefault = new ScriptTask("taskDefault", "return 'default';");
        var end = new EndEvent("end1");

        var activities = new List<Activity> { start, gateway, taskA, taskB, taskDefault, end };

        var flows = new List<SequenceFlow>
        {
            new("seq1", start, gateway),
            new ConditionalSequenceFlow("condA", gateway, taskA, "x > 10"),
            new ConditionalSequenceFlow("condB", gateway, taskB, "x > 5"),
            new("seqA-end", taskA, end),
            new("seqB-end", taskB, end),
            new("seqDefault-end", taskDefault, end)
        };

        if (withDefaultFlow)
            flows.Add(new DefaultSequenceFlow("defaultFlow", gateway, taskDefault));

        var (execution, state, _) = CreateStartedExecution(activities, flows);

        // Complete start -> spawn gateway
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(gateway)])
        ]);

        // Execute gateway (add conditions via ProcessCommands)
        var gatewayEntry = state.GetActiveActivities().First(e => e.ActivityId == "igw1");
        execution.MarkExecuting(gatewayEntry.ActivityInstanceId);

        var commands = new List<IExecutionCommand>
        {
            new AddConditionsCommand(
                ["condA", "condB"],
                [
                    new ConditionEvaluation("condA", "x > 10"),
                    new ConditionEvaluation("condB", "x > 5")
                ])
        };
        execution.ProcessCommands(commands, gatewayEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        return (execution, state, gatewayEntry);
    }

    // ===== ExclusiveGateway: CompleteConditionSequence Tests =====

    [TestMethod]
    public void CompleteConditionSequence_ExclusiveGateway_FirstTrue_ShouldCompleteGateway()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway();

        execution.CompleteConditionSequence("xgw1", "condA", true);

        // Gateway should be completed (short-circuit on first true)
        Assert.IsTrue(gatewayEntry.IsCompleted);

        // Events should include ConditionSequenceEvaluated and ActivityCompleted
        var events = execution.GetUncommittedEvents();
        var evaluated = events.OfType<ConditionSequenceEvaluated>().Single();
        Assert.AreEqual(gatewayEntry.ActivityInstanceId, evaluated.GatewayInstanceId);
        Assert.AreEqual("condA", evaluated.SequenceFlowId);
        Assert.IsTrue(evaluated.Result);

        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(gatewayEntry.ActivityInstanceId, completed.ActivityInstanceId);
    }

    [TestMethod]
    public void CompleteConditionSequence_ExclusiveGateway_FirstFalse_ShouldNotCompleteYet()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway();

        execution.CompleteConditionSequence("xgw1", "condA", false);

        // Gateway should NOT be completed yet (still waiting for condB)
        Assert.IsFalse(gatewayEntry.IsCompleted);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(1, events.Count); // Only ConditionSequenceEvaluated
        Assert.IsInstanceOfType<ConditionSequenceEvaluated>(events[0]);
    }

    [TestMethod]
    public void CompleteConditionSequence_ExclusiveGateway_SecondTrue_ShouldCompleteGateway()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway();

        execution.CompleteConditionSequence("xgw1", "condA", false);
        execution.ClearUncommittedEvents();

        execution.CompleteConditionSequence("xgw1", "condB", true);

        Assert.IsTrue(gatewayEntry.IsCompleted);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(1, events.OfType<ConditionSequenceEvaluated>().Count());
        Assert.AreEqual(1, events.OfType<ActivityCompleted>().Count());
    }

    [TestMethod]
    public void CompleteConditionSequence_ExclusiveGateway_AllFalse_WithDefault_ShouldCompleteGateway()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway(withDefaultFlow: true);

        execution.CompleteConditionSequence("xgw1", "condA", false);
        execution.ClearUncommittedEvents();

        execution.CompleteConditionSequence("xgw1", "condB", false);

        // All conditions false + default flow exists -> gateway should complete
        Assert.IsTrue(gatewayEntry.IsCompleted);

        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(gatewayEntry.ActivityInstanceId, completed.ActivityInstanceId);
    }

    [TestMethod]
    public void CompleteConditionSequence_ExclusiveGateway_AllFalse_NoDefault_ShouldThrow()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway(withDefaultFlow: false);

        execution.CompleteConditionSequence("xgw1", "condA", false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.CompleteConditionSequence("xgw1", "condB", false));
    }

    // ===== InclusiveGateway: CompleteConditionSequence Tests =====

    [TestMethod]
    public void CompleteConditionSequence_InclusiveGateway_FirstTrue_ShouldNotCompleteYet()
    {
        var (execution, state, gatewayEntry) = CreateWithInclusiveGateway();

        // InclusiveGateway never short-circuits: must wait for ALL conditions
        execution.CompleteConditionSequence("igw1", "condA", true);

        // Gateway should NOT be completed yet
        Assert.IsFalse(gatewayEntry.IsCompleted);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(1, events.Count); // Only ConditionSequenceEvaluated
    }

    [TestMethod]
    public void CompleteConditionSequence_InclusiveGateway_BothTrue_ShouldCompleteGateway()
    {
        var (execution, state, gatewayEntry) = CreateWithInclusiveGateway();

        execution.CompleteConditionSequence("igw1", "condA", true);
        execution.ClearUncommittedEvents();

        execution.CompleteConditionSequence("igw1", "condB", true);

        Assert.IsTrue(gatewayEntry.IsCompleted);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(1, events.OfType<ActivityCompleted>().Count());
    }

    [TestMethod]
    public void CompleteConditionSequence_InclusiveGateway_OneTrueOneFalse_ShouldCompleteGateway()
    {
        var (execution, state, gatewayEntry) = CreateWithInclusiveGateway();

        execution.CompleteConditionSequence("igw1", "condA", true);
        execution.ClearUncommittedEvents();

        execution.CompleteConditionSequence("igw1", "condB", false);

        // At least one true -> complete
        Assert.IsTrue(gatewayEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteConditionSequence_InclusiveGateway_AllFalse_WithDefault_ShouldCompleteGateway()
    {
        var (execution, state, gatewayEntry) = CreateWithInclusiveGateway(withDefaultFlow: true);

        execution.CompleteConditionSequence("igw1", "condA", false);
        execution.ClearUncommittedEvents();

        execution.CompleteConditionSequence("igw1", "condB", false);

        // All false but default exists -> complete
        Assert.IsTrue(gatewayEntry.IsCompleted);
    }

    [TestMethod]
    public void CompleteConditionSequence_InclusiveGateway_AllFalse_NoDefault_ShouldThrow()
    {
        var (execution, state, gatewayEntry) = CreateWithInclusiveGateway(withDefaultFlow: false);

        execution.CompleteConditionSequence("igw1", "condA", false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.CompleteConditionSequence("igw1", "condB", false));
    }

    // ===== CompleteConditionSequence: Edge Cases =====

    [TestMethod]
    public void CompleteConditionSequence_NoActiveEntry_ShouldThrow()
    {
        var start = new StartEvent("start1");
        var gateway = new ExclusiveGateway("xgw1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, gateway, end],
            [new("seq1", start, gateway), new("seq2", gateway, end)]);

        // There's no active entry for "xgw1" at this point
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.CompleteConditionSequence("xgw1", "condA", true));
    }

    [TestMethod]
    public void CompleteConditionSequence_NotAConditionalGateway_ShouldThrow()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end],
            [new("seq1", start, task), new("seq2", task, end)]);

        // Spawn task via completing start
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(task)])
        ]);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.CompleteConditionSequence("task1", "condA", true));
    }

    // ===== Apply: ConditionSequenceEvaluated =====

    [TestMethod]
    public void Apply_ConditionSequenceEvaluated_ShouldUpdateState()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway();

        execution.CompleteConditionSequence("xgw1", "condA", true);

        // Verify the condition sequence state was updated
        var sequences = state.GetConditionSequenceStatesForGateway(gatewayEntry.ActivityInstanceId).ToList();
        var condA = sequences.First(s => s.ConditionalSequenceFlowId == "condA");
        Assert.IsTrue(condA.IsEvaluated);
        Assert.IsTrue(condA.Result);

        // condB should still be unevaluated
        var condB = sequences.First(s => s.ConditionalSequenceFlowId == "condB");
        Assert.IsFalse(condB.IsEvaluated);
    }

    // ===== Apply: ParentInfoSet =====

    [TestMethod]
    public void SetParentInfo_ShouldEmitParentInfoSetAndUpdateState()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "child-wf",
            Activities = [start, end],
            SequenceFlows = [new("seq1", start, end)],
            ProcessDefinitionId = "child-pd"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        execution.ClearUncommittedEvents();

        var parentId = Guid.NewGuid();
        execution.SetParentInfo(parentId, "callActivity1");

        // Verify event emitted
        var events = execution.GetUncommittedEvents();
        var parentInfoSet = events.OfType<ParentInfoSet>().Single();
        Assert.AreEqual(parentId, parentInfoSet.ParentInstanceId);
        Assert.AreEqual("callActivity1", parentInfoSet.ParentActivityId);

        // Verify state updated
        Assert.AreEqual(parentId, state.ParentWorkflowInstanceId);
        Assert.AreEqual("callActivity1", state.ParentActivityId);
    }

    // ===== OnChildWorkflowCompleted Tests =====

    [TestMethod]
    public void OnChildWorkflowCompleted_ShouldCompleteCallActivityWithMappedVariables()
    {
        var start = new StartEvent("start1");
        var callActivity = new CallActivity(
            "call1", "child-process",
            InputMappings: [],
            OutputMappings: [new VariableMapping("childResult", "parentResult")],
            PropagateAllChildVariables: false);
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, callActivity, end],
            [new("seq1", start, callActivity), new("seq2", callActivity, end)]);

        // Complete start -> spawn call activity
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(callActivity)])
        ]);

        var callEntry = state.GetActiveActivities().First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        // Simulate child workflow completion with variables
        var childVars = new ExpandoObject();
        ((IDictionary<string, object?>)childVars)["childResult"] = 42;
        ((IDictionary<string, object?>)childVars)["childInternal"] = "secret";

        var effects = execution.OnChildWorkflowCompleted("call1", childVars);

        // Call activity should be completed
        Assert.IsTrue(callEntry.IsCompleted);

        // Variables should be mapped according to output mappings
        var mergedVars = state.GetMergedVariables(callEntry.VariablesId);
        var dict = (IDictionary<string, object?>)mergedVars;
        Assert.AreEqual(42, dict["parentResult"]);
        // PropagateAllChildVariables is false, so childInternal should NOT be propagated
        Assert.IsFalse(dict.ContainsKey("childInternal"));

        // Events should include ActivityCompleted
        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(callEntry.ActivityInstanceId, completed.ActivityInstanceId);
    }

    [TestMethod]
    public void OnChildWorkflowCompleted_PropagateAllChildVariables_ShouldPropagateAll()
    {
        var start = new StartEvent("start1");
        var callActivity = new CallActivity(
            "call1", "child-process",
            InputMappings: [],
            OutputMappings: [],
            PropagateAllChildVariables: true);
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, callActivity, end],
            [new("seq1", start, callActivity), new("seq2", callActivity, end)]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(callActivity)])
        ]);

        var callEntry = state.GetActiveActivities().First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        var childVars = new ExpandoObject();
        ((IDictionary<string, object?>)childVars)["varA"] = "hello";
        ((IDictionary<string, object?>)childVars)["varB"] = 99;

        execution.OnChildWorkflowCompleted("call1", childVars);

        var mergedVars = state.GetMergedVariables(callEntry.VariablesId);
        var dict = (IDictionary<string, object?>)mergedVars;
        Assert.AreEqual("hello", dict["varA"]);
        Assert.AreEqual(99, dict["varB"]);
    }

    [TestMethod]
    public void OnChildWorkflowCompleted_NotACallActivity_ShouldThrow()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end],
            [new("seq1", start, task), new("seq2", task, end)]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(task)])
        ]);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.OnChildWorkflowCompleted("task1", new ExpandoObject()));
    }

    [TestMethod]
    public void OnChildWorkflowCompleted_WithBoundaryTimer_ShouldReturnUnsubscribeEffects()
    {
        var start = new StartEvent("start1");
        var callActivity = new CallActivity(
            "call1", "child-process",
            InputMappings: [],
            OutputMappings: [],
            PropagateAllChildVariables: true);
        var boundaryTimer = new BoundaryTimerEvent(
            "boundary-timer1", "call1",
            new TimerDefinition(TimerType.Duration, "PT30S"));
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, callActivity, end, boundaryTimer],
            [
                new("seq1", start, callActivity),
                new("seq2", callActivity, end)
            ]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(callActivity)])
        ]);

        var callEntry = state.GetActiveActivities().First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        var effects = execution.OnChildWorkflowCompleted("call1", new ExpandoObject());

        // Should have an UnregisterTimerEffect for the boundary timer
        var timerEffect = effects.OfType<UnregisterTimerEffect>().Single();
        Assert.AreEqual(state.Id, timerEffect.WorkflowInstanceId);
        Assert.AreEqual(callEntry.ActivityInstanceId, timerEffect.HostActivityInstanceId);
        Assert.AreEqual("boundary-timer1", timerEffect.TimerActivityId);
    }

    // ===== OnChildWorkflowFailed Tests =====

    [TestMethod]
    public void OnChildWorkflowFailed_ShouldFailCallActivity()
    {
        var start = new StartEvent("start1");
        var callActivity = new CallActivity(
            "call1", "child-process",
            InputMappings: [],
            OutputMappings: []);
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, callActivity, end],
            [new("seq1", start, callActivity), new("seq2", callActivity, end)]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(callActivity)])
        ]);

        var callEntry = state.GetActiveActivities().First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        var exception = new Exception("child process crashed");
        var effects = execution.OnChildWorkflowFailed("call1", exception);

        // Call activity should be failed
        Assert.IsTrue(callEntry.IsCompleted);
        Assert.AreEqual(500, callEntry.ErrorCode);
        Assert.AreEqual("child process crashed", callEntry.ErrorMessage);

        // Events should include ActivityFailed
        var events = execution.GetUncommittedEvents();
        var failed = events.OfType<ActivityFailed>().Single();
        Assert.AreEqual(callEntry.ActivityInstanceId, failed.ActivityInstanceId);
    }

    [TestMethod]
    public void OnChildWorkflowFailed_WithBoundaryErrorHandler_ShouldSpawnBoundaryEvent()
    {
        var start = new StartEvent("start1");
        var callActivity = new CallActivity(
            "call1", "child-process",
            InputMappings: [],
            OutputMappings: []);
        var boundaryError = new BoundaryErrorEvent("boundary-error1", "call1", "500");
        var errorHandler = new ScriptTask("errorHandler1", "return 'handled';");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, callActivity, end, boundaryError, errorHandler],
            [
                new("seq1", start, callActivity),
                new("seq2", callActivity, end),
                new("seq3", boundaryError, errorHandler)
            ]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(callActivity)])
        ]);

        var callEntry = state.GetActiveActivities().First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        var effects = execution.OnChildWorkflowFailed("call1", new Exception("child failed"));

        // Should have spawned the boundary error event
        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("boundary-error1", spawned.ActivityId);
        Assert.AreEqual("BoundaryErrorEvent", spawned.ActivityType);
    }

    [TestMethod]
    public void OnChildWorkflowFailed_AlreadyCompleted_ShouldReturnEmpty()
    {
        var start = new StartEvent("start1");
        var callActivity = new CallActivity(
            "call1", "child-process",
            InputMappings: [],
            OutputMappings: []);
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, callActivity, end],
            [new("seq1", start, callActivity), new("seq2", callActivity, end)]);

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(callActivity)])
        ]);

        var callEntry = state.GetActiveActivities().First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);

        // Complete first
        execution.OnChildWorkflowCompleted("call1", new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Then fail (stale callback)
        var effects = execution.OnChildWorkflowFailed("call1", new Exception("late failure"));

        Assert.AreEqual(0, effects.Count);
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    // ===== Integration: ExclusiveGateway condition sequence state flow =====

    [TestMethod]
    public void CompleteConditionSequence_ExclusiveGateway_StateReflectsEachEvaluation()
    {
        var (execution, state, gatewayEntry) = CreateWithExclusiveGateway();

        // Before any evaluation, both conditions should be unevaluated
        var sequences = state.GetConditionSequenceStatesForGateway(gatewayEntry.ActivityInstanceId).ToList();
        Assert.AreEqual(2, sequences.Count);
        Assert.IsTrue(sequences.All(s => !s.IsEvaluated));

        // Evaluate condA as false
        execution.CompleteConditionSequence("xgw1", "condA", false);

        sequences = state.GetConditionSequenceStatesForGateway(gatewayEntry.ActivityInstanceId).ToList();
        var condA = sequences.First(s => s.ConditionalSequenceFlowId == "condA");
        var condB = sequences.First(s => s.ConditionalSequenceFlowId == "condB");
        Assert.IsTrue(condA.IsEvaluated);
        Assert.IsFalse(condA.Result);
        Assert.IsFalse(condB.IsEvaluated);

        execution.ClearUncommittedEvents();

        // Evaluate condB as true
        execution.CompleteConditionSequence("xgw1", "condB", true);

        condB = sequences.First(s => s.ConditionalSequenceFlowId == "condB");
        Assert.IsTrue(condB.IsEvaluated);
        Assert.IsTrue(condB.Result);

        // Gateway is completed
        Assert.IsTrue(gatewayEntry.IsCompleted);
    }

    // ===== Integration: InclusiveGateway condition sequence state flow =====

    [TestMethod]
    public void CompleteConditionSequence_InclusiveGateway_StateReflectsAllEvaluations()
    {
        var (execution, state, gatewayEntry) = CreateWithInclusiveGateway();

        // Evaluate condA as true - not complete yet (inclusive waits for all)
        execution.CompleteConditionSequence("igw1", "condA", true);
        Assert.IsFalse(gatewayEntry.IsCompleted);

        execution.ClearUncommittedEvents();

        // Evaluate condB as false - now all evaluated, at least one true -> complete
        execution.CompleteConditionSequence("igw1", "condB", false);
        Assert.IsTrue(gatewayEntry.IsCompleted);

        // Verify final condition states
        var sequences = state.GetConditionSequenceStatesForGateway(gatewayEntry.ActivityInstanceId).ToList();
        Assert.IsTrue(sequences.All(s => s.IsEvaluated));
        Assert.AreEqual(1, sequences.Count(s => s.Result));
    }
}
