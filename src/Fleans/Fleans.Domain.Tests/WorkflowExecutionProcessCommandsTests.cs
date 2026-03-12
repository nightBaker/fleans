using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionProcessCommandsTests
{
    private static (WorkflowExecution execution, WorkflowInstanceState state, Guid startInstanceId) CreateStartedExecution(
        List<Activity>? extraActivities = null,
        List<SequenceFlow>? extraFlows = null,
        List<MessageDefinition>? messages = null,
        List<SignalDefinition>? signals = null)
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var activities = new List<Activity> { start, end };
        if (extraActivities is not null)
            activities.AddRange(extraActivities);

        var flows = new List<SequenceFlow> { new("seq1", start, end) };
        if (extraFlows is not null)
            flows.AddRange(extraFlows);

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
        var startInstanceId = state.Entries.First().ActivityInstanceId;
        execution.MarkExecuting(startInstanceId);
        execution.ClearUncommittedEvents();
        return (execution, state, startInstanceId);
    }

    // --- CompleteWorkflowCommand ---

    [TestMethod]
    public void ProcessCommands_CompleteWorkflowCommand_ShouldEmitWorkflowCompleted()
    {
        var (execution, state, id) = CreateStartedExecution();

        var effects = execution.ProcessCommands([new CompleteWorkflowCommand()], id);

        Assert.AreEqual(0, effects.Count);
        var events = execution.GetUncommittedEvents();
        Assert.IsInstanceOfType<WorkflowCompleted>(events.Single());
        Assert.IsTrue(state.IsCompleted);
    }

    // --- SpawnActivityCommand (regular) ---

    [TestMethod]
    public void ProcessCommands_SpawnActivityCommand_ShouldEmitActivitySpawned()
    {
        var scriptTask = new ScriptTask("task1", "return 1;");
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [scriptTask]);

        var effects = execution.ProcessCommands(
            [new SpawnActivityCommand(scriptTask, null, null)], id);

        Assert.AreEqual(0, effects.Count);
        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("task1", spawned.ActivityId);
        Assert.AreEqual("ScriptTask", spawned.ActivityType);
        // VariablesId should come from the parent activity
        var parentEntry = state.GetActiveEntry(id);
        Assert.AreEqual(parentEntry.VariablesId, spawned.VariablesId);
    }

    [TestMethod]
    public void ProcessCommands_SpawnActivityCommand_ShouldCreateNewEntry()
    {
        var scriptTask = new ScriptTask("task1", "return 1;");
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [scriptTask]);

        execution.ProcessCommands([new SpawnActivityCommand(scriptTask, null, null)], id);

        // Should have the start entry + the new task entry
        Assert.AreEqual(2, state.Entries.Count);
        var newEntry = state.Entries.First(e => e.ActivityId == "task1");
        Assert.IsFalse(newEntry.IsCompleted);
        Assert.IsFalse(newEntry.IsExecuting);
    }

    // --- SpawnActivityCommand (multi-instance iteration) ---

    [TestMethod]
    public void ProcessCommands_SpawnActivityCommand_MultiInstance_ShouldCreateChildScopeAndSetVariables()
    {
        var scriptTask = new ScriptTask("task1", "return 1;");
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [scriptTask]);

        var parentVariablesId = state.VariableStates.First().Id;

        var cmd = new SpawnActivityCommand(scriptTask, null, null)
        {
            MultiInstanceIndex = 0,
            ParentVariablesId = parentVariablesId,
            IterationItem = "itemValue",
            IterationItemName = "item"
        };

        var effects = execution.ProcessCommands([cmd], id);

        Assert.AreEqual(0, effects.Count);
        var events = execution.GetUncommittedEvents();

        // Should have: ChildVariableScopeCreated, VariablesMerged (loopCounter + item), ActivitySpawned
        var scopeCreated = events.OfType<ChildVariableScopeCreated>().Single();
        Assert.AreEqual(parentVariablesId, scopeCreated.ParentScopeId);

        var varsMerged = events.OfType<VariablesMerged>().Single();
        Assert.AreEqual(scopeCreated.ScopeId, varsMerged.VariablesId);
        var mergedDict = (IDictionary<string, object?>)varsMerged.Variables;
        Assert.AreEqual(0, mergedDict["loopCounter"]);
        Assert.AreEqual("itemValue", mergedDict["item"]);

        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(0, spawned.MultiInstanceIndex);
        Assert.AreEqual(scopeCreated.ScopeId, spawned.VariablesId);
    }

    [TestMethod]
    public void ProcessCommands_SpawnActivityCommand_MultiInstance_NoItemName_ShouldOnlySetLoopCounter()
    {
        var scriptTask = new ScriptTask("task1", "return 1;");
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [scriptTask]);

        var parentVariablesId = state.VariableStates.First().Id;

        var cmd = new SpawnActivityCommand(scriptTask, null, null)
        {
            MultiInstanceIndex = 2,
            ParentVariablesId = parentVariablesId
        };

        var effects = execution.ProcessCommands([cmd], id);

        var events = execution.GetUncommittedEvents();
        var varsMerged = events.OfType<VariablesMerged>().Single();
        var mergedDict = (IDictionary<string, object?>)varsMerged.Variables;
        Assert.AreEqual(2, mergedDict["loopCounter"]);
        Assert.IsFalse(mergedDict.ContainsKey("item"));
    }

    // --- OpenSubProcessCommand ---

    [TestMethod]
    public void ProcessCommands_OpenSubProcessCommand_ShouldCreateChildScopeAndSpawnStartEvent()
    {
        var subStart = new StartEvent("subStart1");
        var subEnd = new EndEvent("subEnd1");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [subStart, subEnd],
            SequenceFlows = [new SequenceFlow("subSeq1", subStart, subEnd)]
        };
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [subProcess]);

        var parentVarId = state.VariableStates.First().Id;

        var effects = execution.ProcessCommands(
            [new OpenSubProcessCommand(subProcess, parentVarId)], id);

        Assert.AreEqual(0, effects.Count);
        var events = execution.GetUncommittedEvents();

        var scopeCreated = events.OfType<ChildVariableScopeCreated>().Single();
        Assert.AreEqual(parentVarId, scopeCreated.ParentScopeId);

        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("subStart1", spawned.ActivityId);
        Assert.AreEqual(scopeCreated.ScopeId, spawned.VariablesId);
    }

    // --- RegisterTimerCommand ---

    [TestMethod]
    public void ProcessCommands_RegisterTimerCommand_ShouldReturnRegisterTimerEffect()
    {
        var (execution, state, id) = CreateStartedExecution();

        var effects = execution.ProcessCommands(
            [new RegisterTimerCommand("timer1", TimeSpan.FromSeconds(30), false)], id);

        Assert.AreEqual(1, effects.Count);
        var timerEffect = (RegisterTimerEffect)effects[0];
        Assert.AreEqual(state.Id, timerEffect.WorkflowInstanceId);
        Assert.AreEqual(id, timerEffect.HostActivityInstanceId);
        Assert.AreEqual("timer1", timerEffect.TimerActivityId);
        Assert.AreEqual(TimeSpan.FromSeconds(30), timerEffect.DueTime);

        // No domain events emitted
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    // --- RegisterMessageCommand ---

    [TestMethod]
    public void ProcessCommands_RegisterMessageCommand_ShouldReturnSubscribeMessageEffect()
    {
        var msgDef = new MessageDefinition("msg1", "OrderCreated", "= orderId");
        var (execution, state, id) = CreateStartedExecution(
            messages: [msgDef]);

        // Set a variable so correlation key can be resolved
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["orderId"] = "order-123";
        state.MergeState(varsId, vars);

        var effects = execution.ProcessCommands(
            [new RegisterMessageCommand(varsId, "msg1", "msgActivity1", false)], id);

        Assert.AreEqual(1, effects.Count);
        var msgEffect = (SubscribeMessageEffect)effects[0];
        Assert.AreEqual("OrderCreated", msgEffect.MessageName);
        Assert.AreEqual("order-123", msgEffect.CorrelationKey);
        Assert.AreEqual(state.Id, msgEffect.WorkflowInstanceId);
        Assert.AreEqual(id, msgEffect.HostActivityInstanceId);
    }

    // --- RegisterSignalCommand ---

    [TestMethod]
    public void ProcessCommands_RegisterSignalCommand_ShouldReturnSubscribeSignalEffect()
    {
        var (execution, state, id) = CreateStartedExecution();

        var effects = execution.ProcessCommands(
            [new RegisterSignalCommand("Signal_Approved", "signalActivity1", false)], id);

        Assert.AreEqual(1, effects.Count);
        var signalEffect = (SubscribeSignalEffect)effects[0];
        Assert.AreEqual("Signal_Approved", signalEffect.SignalName);
        Assert.AreEqual(state.Id, signalEffect.WorkflowInstanceId);
        Assert.AreEqual(id, signalEffect.HostActivityInstanceId);
    }

    // --- StartChildWorkflowCommand ---

    [TestMethod]
    public void ProcessCommands_StartChildWorkflowCommand_ShouldReturnStartChildWorkflowEffect()
    {
        var callActivity = new CallActivity("call1", "childProcess", [], []);
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [callActivity]);

        // Need the call activity to have an active entry for SetChildWorkflowInstanceId
        // Spawn it first, then mark executing
        execution.ProcessCommands([new SpawnActivityCommand(callActivity, null, null)], id);
        var callEntry = state.Entries.First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        var effects = execution.ProcessCommands(
            [new StartChildWorkflowCommand(callActivity)], callEntry.ActivityInstanceId);

        Assert.AreEqual(1, effects.Count);
        var childEffect = (StartChildWorkflowEffect)effects[0];
        Assert.AreEqual("childProcess", childEffect.ProcessDefinitionKey);
        Assert.AreNotEqual(Guid.Empty, childEffect.ChildInstanceId);
        Assert.AreEqual("call1", childEffect.ParentActivityId);

        // Entry should have the child instance ID set
        Assert.AreEqual(childEffect.ChildInstanceId, callEntry.ChildWorkflowInstanceId);
    }

    [TestMethod]
    public void ProcessCommands_StartChildWorkflowCommand_ShouldBuildInputVariables()
    {
        var callActivity = new CallActivity(
            "call1", "childProcess",
            [new VariableMapping("sourceVar", "targetVar")],
            [],
            PropagateAllParentVariables: false);
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [callActivity]);

        // Set variables
        var varsId = state.VariableStates.First().Id;
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["sourceVar"] = "hello";
        ((IDictionary<string, object?>)vars)["otherVar"] = "world";
        state.MergeState(varsId, vars);

        // Spawn and execute the call activity
        execution.ProcessCommands([new SpawnActivityCommand(callActivity, null, null)], id);
        var callEntry = state.Entries.First(e => e.ActivityId == "call1");
        execution.MarkExecuting(callEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();

        var effects = execution.ProcessCommands(
            [new StartChildWorkflowCommand(callActivity)], callEntry.ActivityInstanceId);

        var childEffect = (StartChildWorkflowEffect)effects[0];
        var inputDict = (IDictionary<string, object?>)childEffect.InputVariables;
        Assert.AreEqual("hello", inputDict["targetVar"]);
        Assert.IsFalse(inputDict.ContainsKey("otherVar")); // not propagated
    }

    // --- AddConditionsCommand ---

    [TestMethod]
    public void ProcessCommands_AddConditionsCommand_ShouldEmitConditionSequencesAddedAndReturnEffects()
    {
        var (execution, state, id) = CreateStartedExecution();

        var evaluations = new List<ConditionEvaluation>
        {
            new("flow1", "x > 5"),
            new("flow2", "x <= 5")
        };
        var cmd = new AddConditionsCommand(["flow1", "flow2"], evaluations);

        var effects = execution.ProcessCommands([cmd], id);

        // Domain event
        var events = execution.GetUncommittedEvents();
        var added = events.OfType<ConditionSequencesAdded>().Single();
        Assert.AreEqual(id, added.GatewayInstanceId);
        CollectionAssert.AreEqual(new[] { "flow1", "flow2" }, added.SequenceFlowIds);

        // State should have condition sequences
        var sequences = state.GetConditionSequenceStatesForGateway(id).ToList();
        Assert.AreEqual(2, sequences.Count);

        // Infrastructure effects: one PublishDomainEventEffect per evaluation
        Assert.AreEqual(2, effects.Count);
        var effect1 = (PublishDomainEventEffect)effects[0];
        var evalEvent1 = (EvaluateConditionEvent)effect1.Event;
        Assert.AreEqual("flow1", evalEvent1.SequenceFlowId);
        Assert.AreEqual("x > 5", evalEvent1.Condition);
        Assert.AreEqual(state.Id, evalEvent1.WorkflowInstanceId);
        Assert.AreEqual(id, evalEvent1.ActivityInstanceId);
    }

    // --- ThrowSignalCommand ---

    [TestMethod]
    public void ProcessCommands_ThrowSignalCommand_ShouldReturnThrowSignalEffect()
    {
        var (execution, state, id) = CreateStartedExecution();

        var effects = execution.ProcessCommands(
            [new ThrowSignalCommand("Signal_Complete")], id);

        Assert.AreEqual(1, effects.Count);
        var signalEffect = (ThrowSignalEffect)effects[0];
        Assert.AreEqual("Signal_Complete", signalEffect.SignalName);

        // No domain events
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }

    // --- Multiple commands ---

    [TestMethod]
    public void ProcessCommands_MultipleCommands_ShouldProcessAllAndAggregateEffects()
    {
        var scriptTask = new ScriptTask("task1", "return 1;");
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [scriptTask]);

        var effects = execution.ProcessCommands([
            new SpawnActivityCommand(scriptTask, null, null),
            new RegisterTimerCommand("timer1", TimeSpan.FromSeconds(10), false),
            new ThrowSignalCommand("Signal_Done")
        ], id);

        // SpawnActivity -> 0 effects, RegisterTimer -> 1 effect, ThrowSignal -> 1 effect
        Assert.AreEqual(2, effects.Count);
        Assert.IsInstanceOfType<RegisterTimerEffect>(effects[0]);
        Assert.IsInstanceOfType<ThrowSignalEffect>(effects[1]);

        // Domain events: ActivitySpawned from SpawnActivity
        var events = execution.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<ActivitySpawned>().Any());
    }

    // --- SpawnActivityCommand with ScopeId ---

    [TestMethod]
    public void ProcessCommands_SpawnActivityCommand_WithScopeId_ShouldSetScopeOnEntry()
    {
        var scriptTask = new ScriptTask("task1", "return 1;");
        var scopeId = Guid.NewGuid();
        var (execution, state, id) = CreateStartedExecution(
            extraActivities: [scriptTask]);

        execution.ProcessCommands(
            [new SpawnActivityCommand(scriptTask, scopeId, null)], id);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(scopeId, spawned.ScopeId);
    }
}
