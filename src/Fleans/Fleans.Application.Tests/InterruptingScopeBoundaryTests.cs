using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Interrupting message and signal boundaries on scopes (a sub-process, a call activity) and a single interrupting
/// timer on a user task: the boundary path is taken, the scope's waiting children are cancelled, and a late
/// completion of a cancelled child changes nothing. Releasing a call activity's child instance is #847
/// (<see cref="ScopeCancellationCleanupTests"/>).
/// </summary>
[TestClass]
public class InterruptingScopeBoundaryTests : WorkflowTestBase
{
    [TestMethod]
    public async Task MessageBoundary_OnASubProcess_CancelsTheWaitingChild_AndTakesTheBoundaryPath()
    {
        // Arrange
        var orderId = $"order-{Guid.NewGuid():N}";
        var workflow = SubProcessWith(
            id => new MessageBoundaryEvent(id, "sub", "cancelMsg"),
            messages: [new MessageDefinition("cancelMsg", "cancelSubOrder", "orderId")]);
        var (instance, instanceId) = await Start(workflow, orderId);
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("inner"));

        // Act
        Assert.IsTrue(await DeliverMessageWhenSubscribed("cancelSubOrder", orderId));

        // Assert
        await AssertInterrupted(instance, instanceId);
    }

    [TestMethod]
    public async Task SignalBoundary_OnASubProcess_CancelsTheWaitingChild_AndTakesTheBoundaryPath()
    {
        // Arrange
        var signalName = $"abortSub-{Guid.NewGuid():N}";
        var workflow = SubProcessWith(
            id => new SignalBoundaryEvent(id, "sub", "abortSig"),
            signals: [new SignalDefinition("abortSig", signalName)]);
        var (instance, instanceId) = await Start(workflow, "unused");
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("inner"));

        // Act
        Assert.AreEqual(1, await BroadcastSignalWhenSubscribed(signalName));

        // Assert
        await AssertInterrupted(instance, instanceId);
    }

    [TestMethod]
    public async Task MessageBoundary_OnACallActivity_TakesTheBoundaryPath()
    {
        // Arrange
        var orderId = $"order-{Guid.NewGuid():N}";
        var workflow = await CallActivityWith("msg",
            id => new MessageBoundaryEvent(id, "call", "cancelMsg"),
            messages: [new MessageDefinition("cancelMsg", "cancelCallOrder", "orderId")]);
        var (_, instanceId) = await Start(workflow, orderId);
        await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == "call" && a.ChildWorkflowInstanceId is not null));

        // Act
        Assert.IsTrue(await DeliverMessageWhenSubscribed("cancelCallOrder", orderId));

        // Assert
        await AssertCallInterrupted(instanceId);
    }

    [TestMethod]
    public async Task SignalBoundary_OnACallActivity_TakesTheBoundaryPath()
    {
        // Arrange
        var signalName = $"abortCall-{Guid.NewGuid():N}";
        var workflow = await CallActivityWith("sig",
            id => new SignalBoundaryEvent(id, "call", "abortSig"),
            signals: [new SignalDefinition("abortSig", signalName)]);
        var (_, instanceId) = await Start(workflow, "unused");
        await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == "call" && a.ChildWorkflowInstanceId is not null));

        // Act
        Assert.AreEqual(1, await BroadcastSignalWhenSubscribed(signalName));

        // Assert
        await AssertCallInterrupted(instanceId);
    }

    [TestMethod]
    public async Task TimerBoundary_OnAUserTask_TakesTheTimeoutPath_AndTheTaskLeavesTheTaskList()
    {
        // Arrange — start → approve(user task, 30 min interrupting timer → escalated → escalated_end) → end
        var start = new StartEvent("start");
        var approve = new UserTask("approve", "john", [], [], null);
        var timeout = new BoundaryTimerEvent("timeout", "approve", new TimerDefinition(TimerType.Duration, "PT30M"));
        var escalated = new TaskActivity("escalated");
        var end = new EndEvent("end");
        var escalatedEnd = new EndEvent("escalated_end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "user-task-timeout",
            Activities = [start, approve, timeout, escalated, end, escalatedEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, approve),
                new SequenceFlow("f2", approve, end),
                new SequenceFlow("f3", timeout, escalated),
                new SequenceFlow("f4", escalated, escalatedEnd)
            ]
        };
        var (instance, instanceId) = await Start(workflow, "unused");
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("approve"));
        var userTaskId = running.ActiveActivities.Single(a => a.ActivityId == "approve").ActivityInstanceId;
        Assert.AreEqual(1, (await QueryService.GetActiveUserTasksForWorkflow(instanceId)).Count);

        // Act
        await instance.HandleTimerFired("timeout", userTaskId);

        // Assert
        var escalating = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("escalated"));
        Assert.IsTrue(escalating.CompletedActivities.Single(a => a.ActivityId == "approve").IsCancelled);
        Assert.AreEqual(0, (await QueryService.GetActiveUserTasksForWorkflow(instanceId)).Count,
            "the interrupted user task is not in the instance's task list");
        var pending = await QueryService.GetPendingUserTasks("john", null, new PageRequest(1, 100));
        Assert.IsFalse(pending.Items.Any(t => t.ActivityInstanceId == userTaskId), "the interrupted user task is not pending");
        await instance.CompleteActivity("escalated", new ExpandoObject());
        var done = await WaitForCondition(instanceId, s => s.IsCompleted);
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "end");
    }

    // --- builders ---

    /// <summary>start → sub(inner task) → end, with an interrupting boundary on sub → recovery → recovery_end.</summary>
    private static WorkflowDefinition SubProcessWith(Func<string, Activity> boundary,
        List<MessageDefinition>? messages = null, List<SignalDefinition>? signals = null)
    {
        var subStart = new StartEvent("subStart");
        var inner = new TaskActivity("inner");
        var subEnd = new EndEvent("subEnd");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, inner, subEnd],
            SequenceFlows = [new SequenceFlow("sf1", subStart, inner), new SequenceFlow("sf2", inner, subEnd)]
        };
        return Around(sub, boundary("onAbort"), $"sub-boundary-{Guid.NewGuid():N}", messages, signals);
    }

    private async Task<WorkflowDefinition> CallActivityWith(string kind, Func<string, Activity> boundary,
        List<MessageDefinition>? messages = null, List<SignalDefinition>? signals = null)
    {
        var childId = $"boundary-child-{kind}";
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");
        var child = new WorkflowDefinition
        {
            WorkflowId = childId,
            Activities = [childStart, childTask, childEnd],
            SequenceFlows = [new SequenceFlow("cs1", childStart, childTask), new SequenceFlow("cs2", childTask, childEnd)]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(childId).DeployVersion(child, "<xml/>");
        return Around(new CallActivity("call", childId, [], []), boundary("onAbort"), $"call-boundary-{kind}", messages, signals);
    }

    private static WorkflowDefinition Around(Activity host, Activity boundary, string id,
        List<MessageDefinition>? messages, List<SignalDefinition>? signals)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var recoveryEnd = new EndEvent("recovery_end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, host, end, boundary, recovery, recoveryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, host),
                new SequenceFlow("f2", host, end),
                new SequenceFlow("f3", boundary, recovery),
                new SequenceFlow("f4", recovery, recoveryEnd)
            ],
            Messages = messages ?? [],
            Signals = signals ?? []
        };
    }

    // --- helpers ---

    private async Task<(IWorkflowInstanceGrain, Guid)> Start(WorkflowDefinition workflow, string orderId)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        dynamic vars = new ExpandoObject();
        vars.orderId = orderId;
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);
        return (instance, instance.GetPrimaryKey());
    }

    private async Task AssertInterrupted(IWorkflowInstanceGrain instance, Guid instanceId)
    {
        var recovering = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("recovery"));
        Assert.IsTrue(recovering.CompletedActivities.Single(a => a.ActivityId == "sub").IsCancelled, "the sub-process is interrupted");
        Assert.IsTrue(recovering.CompletedActivities.Single(a => a.ActivityId == "inner").IsCancelled, "its waiting child is cancelled");

        // A late completion of the cancelled child (e.g. a worker that was already running) changes nothing.
        try
        {
            await instance.CompleteActivity("inner", new ExpandoObject());
        }
        catch (Exception)
        {
            // Rejecting the late completion is fine too.
        }

        await instance.CompleteActivity("recovery", new ExpandoObject());
        var done = await WaitForCondition(instanceId, s => s.IsCompleted);
        CollectionAssert.Contains(done.CompletedActivityIds, "recovery_end");
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "subEnd");
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "end");
    }

    private async Task AssertCallInterrupted(Guid instanceId)
    {
        var recovering = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("recovery"));
        Assert.IsTrue(recovering.CompletedActivities.Single(a => a.ActivityId == "call").IsCancelled, "the call activity is interrupted");
        CollectionAssert.DoesNotContain(recovering.CompletedActivityIds, "end");
    }
}
