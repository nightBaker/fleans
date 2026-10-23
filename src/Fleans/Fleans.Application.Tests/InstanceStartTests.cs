using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// #863 — every start path starts an instance with one grain call and one journal append:
/// WorkflowStarted, the start activity, the parent link, the variables and ExecutionStarted
/// land in the same batch. Each workflow waits on a task right after its start event, so the
/// start is the instance's only append.
/// </summary>
[TestClass]
public class InstanceStartTests : WorkflowTestBase
{
    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    [TestMethod]
    public async Task ApiStart_WithVariablesAndBusinessId_IsOneAppend()
    {
        // Arrange
        await Deploy(Waiting("start-api", new StartEvent("start")));

        // Act
        var instanceId = await CommandService.StartWorkflow("start-api", Variables(), businessId: "ORDER-1");

        // Assert
        var batch = AssertSingleStartAppend(instanceId);
        Assert.AreEqual("ORDER-1", batch.OfType<WorkflowStarted>().Single().BusinessId);
        Assert.AreEqual(1, batch.OfType<VariablesMerged>().Single().Variables.Count(v => v.Key == "amount"));
    }

    [TestMethod]
    public async Task ApiStartByProcessDefinitionId_IsOneAppend()
    {
        // Arrange
        var v1 = await Deploy(Waiting("start-api-version", new StartEvent("start")));
        await Deploy(Waiting("start-api-version", new StartEvent("start")));

        // Act
        var instanceId = await CommandService.StartWorkflowByProcessDefinitionId(v1.ProcessDefinitionId);

        // Assert
        var batch = AssertSingleStartAppend(instanceId);
        Assert.AreEqual(v1.ProcessDefinitionId, batch.OfType<WorkflowStarted>().Single().ProcessDefinitionId);
    }

    [TestMethod]
    public async Task MessageStart_IsOneAppend()
    {
        // Arrange
        var workflow = Waiting("start-message", new MessageStartEvent("msgStart", "msg1")) with
        {
            Messages = [new MessageDefinition("msg1", "startOneAppend", null)]
        };
        await Deploy(workflow);

        // Act
        var result = await CommandService.SendMessage("startOneAppend", null, Variables());

        // Assert
        Assert.IsNotNull(result.WorkflowInstanceIds);
        var batch = AssertSingleStartAppend(result.WorkflowInstanceIds.Single());
        Assert.HasCount(1, batch.OfType<VariablesMerged>());
    }

    [TestMethod]
    public async Task SignalStart_IsOneAppend()
    {
        // Arrange
        var workflow = Waiting("start-signal", new SignalStartEvent("sigStart", "sig1")) with
        {
            Signals = [new SignalDefinition("sig1", "startOneAppendSignal")]
        };
        await Deploy(workflow);

        // Act
        var instanceIds = await Cluster.GrainFactory
            .GetGrain<ISignalStartEventListenerGrain>("startOneAppendSignal").FireSignalStartEvent();

        // Assert
        AssertSingleStartAppend(instanceIds.Single());
    }

    [TestMethod]
    public async Task ConditionalStart_IsOneAppend()
    {
        // Arrange
        await Deploy(Waiting("start-conditional", new ConditionalStartEvent("condStart", "true")));

        // Act
        var instanceId = await Cluster.GrainFactory
            .GetGrain<IConditionalStartEventListenerGrain>("start-conditional:condStart")
            .EvaluateAndStart(Variables());

        // Assert
        Assert.IsNotNull(instanceId);
        var batch = AssertSingleStartAppend(instanceId.Value);
        Assert.HasCount(1, batch.OfType<VariablesMerged>());
    }

    [TestMethod]
    public async Task TimerStart_IsOneAppend()
    {
        // Arrange
        await Deploy(Waiting("start-timer",
            new TimerStartEvent("timerStart", new TimerDefinition(TimerType.Cycle, "R3/PT10M"))));

        // Act
        var instanceId = await Cluster.GrainFactory
            .GetGrain<ITimerStartEventSchedulerGrain>("start-timer").FireTimerStartEvent();

        // Assert
        Assert.IsNotNull(instanceId);
        AssertSingleStartAppend(instanceId.Value);
    }

    [TestMethod]
    public async Task CallActivityChildStart_IsOneAppend_WithParentLinkAndInputs()
    {
        // Arrange
        await Deploy(Waiting("start-child", new StartEvent("childStart")));
        var start = new StartEvent("start");
        var call = new CallActivity("call1", "start-child",
            [new VariableMapping("amount", "amount")], [], PropagateAllParentVariables: false);
        var end = new EndEvent("end");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = "start-parent",
            Activities = [start, call, end],
            SequenceFlows = [new SequenceFlow("p1", start, call), new SequenceFlow("p2", call, end)]
        });

        // Act
        var parentId = await CommandService.StartWorkflow("start-parent", Variables());

        // Assert
        var parentSnapshot = await QueryService.GetStateSnapshot(parentId);
        Assert.IsNotNull(parentSnapshot);
        var childId = parentSnapshot.ActiveActivities.Single(a => a.ActivityId == "call1").ChildWorkflowInstanceId;
        Assert.IsNotNull(childId);
        var batch = AssertSingleStartAppend(childId.Value);
        var link = batch.OfType<ParentInfoSet>().Single();
        Assert.AreEqual(parentId, link.ParentInstanceId);
        Assert.AreEqual("call1", link.ParentActivityId);
        Assert.HasCount(1, batch.OfType<VariablesMerged>());
    }

    /// <summary>
    /// Asserts the instance's journal is one append, ordered created → start activity →
    /// (parent link, variables) → started, and returns that batch.
    /// </summary>
    private static IReadOnlyList<WorkflowEvent> AssertSingleStartAppend(Guid instanceId)
    {
        var appends = FaultInjectingEventStore.AppendsOf(instanceId);
        Assert.HasCount(1, appends, "the start must be one journal append");

        var batch = appends[0];
        Assert.IsInstanceOfType<WorkflowStarted>(batch[0]);
        Assert.AreEqual(instanceId, ((WorkflowStarted)batch[0]).InstanceId);
        Assert.IsInstanceOfType<ActivitySpawned>(batch[1]);
        Assert.HasCount(1, batch.OfType<ExecutionStarted>());
        var startedAt = batch.ToList().FindIndex(e => e is ExecutionStarted);
        Assert.IsTrue(batch.Take(startedAt).All(e => e is WorkflowStarted or ActivitySpawned or ParentInfoSet or VariablesMerged),
            "only creation events come before ExecutionStarted");
        return batch;
    }

    /// <summary>start → task (waits) → end.</summary>
    private static WorkflowDefinition Waiting(string workflowId, Activity start)
    {
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
    }

    private Task<QueryModels.ProcessDefinitionSummary> Deploy(WorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId).DeployVersion(workflow, "<xml/>");

    private static ExpandoObject Variables()
    {
        dynamic variables = new ExpandoObject();
        variables.amount = 42;
        return variables;
    }
}
