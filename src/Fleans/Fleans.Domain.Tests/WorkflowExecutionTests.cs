using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionTests
{
    private static (WorkflowExecution execution, WorkflowInstanceState state) CreateExecution(
        List<Activity> activities, List<SequenceFlow> flows,
        string workflowId = "wf1", string processDefinitionId = "pd1")
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = flows,
            ProcessDefinitionId = processDefinitionId
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        return (execution, state);
    }

    [TestMethod]
    public void Start_ShouldEmitWorkflowStartedAndActivitySpawned()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var (execution, state) = CreateExecution(
            [start, end],
            [new SequenceFlow("seq1", start, end)]);

        execution.Start();

        var events = execution.GetUncommittedEvents();
        Assert.IsTrue(events.Count >= 2);
        Assert.IsInstanceOfType<WorkflowStarted>(events[0]);
        Assert.IsInstanceOfType<ActivitySpawned>(events[1]);
        Assert.AreEqual("start1", ((ActivitySpawned)events[1]).ActivityId);
    }

    [TestMethod]
    public void Start_WithInstanceId_ShouldUseItAsStateId()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);
        var instanceId = Guid.NewGuid();

        execution.Start(instanceId: instanceId);

        var started = (WorkflowStarted)execution.GetUncommittedEvents()[0];
        Assert.AreEqual(instanceId, started.InstanceId);
        Assert.AreEqual(instanceId, state.Id);
    }

    [TestMethod]
    public void Start_WithBusinessId_ShouldCarryItOnWorkflowStartedAndState()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        execution.Start(businessId: "ORDER-42");

        var started = (WorkflowStarted)execution.GetUncommittedEvents()[0];
        Assert.AreEqual("ORDER-42", started.BusinessId);
        Assert.AreEqual("ORDER-42", state.BusinessId);
    }

    [TestMethod]
    public void Start_WithoutBusinessId_ShouldLeaveItNull()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        execution.Start();

        Assert.IsNull(((WorkflowStarted)execution.GetUncommittedEvents()[0]).BusinessId);
        Assert.IsNull(state.BusinessId);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Start_WithBlankBusinessId_ShouldThrowAndEmitNothing(string businessId)
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        Assert.ThrowsExactly<ArgumentException>(() => execution.Start(businessId: businessId));

        Assert.HasCount(0, execution.GetUncommittedEvents());
        Assert.IsNull(state.BusinessId);
    }

    [TestMethod]
    public void Start_WithOverlongBusinessId_ShouldThrow()
    {
        var start = new StartEvent("start1");
        var (execution, _) = CreateExecution([start], []);
        var businessId = new string('x', BusinessIdRules.MaxLength + 1);

        Assert.ThrowsExactly<ArgumentException>(() => execution.Start(businessId: businessId));
        Assert.HasCount(0, execution.GetUncommittedEvents());
    }

    [TestMethod]
    public void Start_WithMaxLengthBusinessId_ShouldSucceed()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);
        var businessId = new string('x', BusinessIdRules.MaxLength);

        execution.Start(businessId: businessId);

        Assert.AreEqual(businessId, state.BusinessId);
    }

    [TestMethod]
    public void Start_ShouldSetStateStarted()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        execution.Start();
        execution.MarkExecutionStarted();

        Assert.IsTrue(state.IsStarted);
        Assert.AreEqual(1, state.Entries.Count);
    }

    [TestMethod]
    public void GetPendingActivities_ShouldReturnNotExecutingNotCompleted()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        execution.Start();

        var pending = execution.GetPendingActivities();
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual("start1", pending[0].ActivityId);
    }

    [TestMethod]
    public void MarkExecuting_ShouldEmitActivityExecutionStarted()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);
        execution.Start();
        var pending = execution.GetPendingActivities();

        execution.MarkExecuting(pending[0].ActivityInstanceId);

        var events = execution.GetUncommittedEvents();
        Assert.IsTrue(events.Any(e => e is ActivityExecutionStarted));
        Assert.AreEqual(0, execution.GetPendingActivities().Count);
    }

    [TestMethod]
    public void ClearUncommittedEvents_ShouldClearList()
    {
        var start = new StartEvent("start1");
        var (execution, _) = CreateExecution([start], []);
        execution.Start();

        Assert.IsTrue(execution.GetUncommittedEvents().Count > 0);
        execution.ClearUncommittedEvents();
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }
}
