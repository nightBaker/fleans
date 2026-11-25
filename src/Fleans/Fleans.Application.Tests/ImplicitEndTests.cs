using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// BPMN §13.2: an activity without an outgoing sequence flow ends its token (implicit end). Once no token
/// is left, the instance completes (#1127).
/// </summary>
[TestClass]
public class ImplicitEndTests : WorkflowTestBase
{
    [TestMethod]
    public async Task BoundaryPath_EndingInATaskWithoutOutgoingFlow_CompletesTheInstance()
    {
        // Arrange — start → approve(user task) → end; interrupting timer on approve → escalated (no outgoing flow)
        var start = new StartEvent("start");
        var approve = new UserTask("approve", "john", [], [], null);
        var timeout = new BoundaryTimerEvent("timeout", "approve", new TimerDefinition(TimerType.Duration, "PT30M"));
        var escalated = new TaskActivity("escalated");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "implicit-end-boundary",
            Activities = [start, approve, timeout, escalated, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, approve),
                new SequenceFlow("f2", approve, end),
                new SequenceFlow("f3", timeout, escalated)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var running = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("approve"));
        var userTaskId = running.ActiveActivities.Single(a => a.ActivityId == "approve").ActivityInstanceId;
        await instance.HandleTimerFired("timeout", userTaskId);
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("escalated"));

        // Act
        await instance.CompleteActivity("escalated", new ExpandoObject());

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(done.IsCompleted, "the implicit end completes the instance");
        Assert.IsFalse(done.IsFailed);
        CollectionAssert.Contains(done.CompletedActivityIds, "escalated");
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task SubProcessPath_EndingInATaskWithoutOutgoingFlow_CompletesTheSubProcess_AndTheInstance()
    {
        // Arrange — start → sub(subStart → work, no end event) → last (no outgoing flow)
        var subStart = new StartEvent("subStart");
        var work = new TaskActivity("work");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, work],
            SequenceFlows = [new SequenceFlow("s1", subStart, work)]
        };
        var start = new StartEvent("start");
        var last = new TaskActivity("last");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "implicit-end-subprocess",
            Activities = [start, sub, last],
            SequenceFlows = [new SequenceFlow("f1", start, sub), new SequenceFlow("f2", sub, last)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("work"));

        // Act
        await instance.CompleteActivity("work", new ExpandoObject());
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("last"));
        await instance.CompleteActivity("last", new ExpandoObject());

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(done.IsCompleted);
        CollectionAssert.IsSubsetOf(new[] { "work", "sub", "last" }, done.CompletedActivityIds.ToList());
    }

    [TestMethod]
    public async Task ParallelBranches_OneEndingImplicitly_CompleteOnlyWhenBothAreDone()
    {
        // Arrange — start → fork → (a, no outgoing) | (b → end)
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var a = new TaskActivity("a");
        var b = new TaskActivity("b");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "implicit-end-parallel",
            Activities = [start, fork, a, b, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, a),
                new SequenceFlow("f3", fork, b),
                new SequenceFlow("f4", b, end)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("a") && s.ActiveActivityIds.Contains("b"));

        // Act
        await instance.CompleteActivity("a", new ExpandoObject());
        var afterA = await WaitForCondition(instance.GetPrimaryKey(), s => s.CompletedActivityIds.Contains("a"));

        // Assert — b's token is still alive
        Assert.IsFalse(afterA.IsCompleted);
        await instance.CompleteActivity("b", new ExpandoObject());
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(done.IsCompleted);
    }
}
