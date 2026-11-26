using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Cancelling a scope that holds waiting elements: an interrupting boundary or event sub-process
/// must release everything the cancelled children registered — message subscriptions, user tasks
/// in the task list, a call activity's child instance — not just mark the entries cancelled.
/// </summary>
[TestClass]
public class ScopeCancellationCleanupTests : WorkflowTestBase
{
    private static readonly TimerDefinition HalfHour = new(TimerType.Duration, "PT30M");

    // --- Message catch inside an interrupted sub-process (#847) ---

    [TestMethod]
    public async Task InterruptedSubProcess_ReleasesItsMessageCatch_SoTheSameKeyCanBeAwaitedAgain()
    {
        // Arrange — start → awaitPayment(sub: wait for payment/<orderId>) with a timer boundary → timedOut
        var orderId = $"order-{Guid.NewGuid():N}";
        var subStart = new StartEvent("subStart");
        var waitPayment = new MessageIntermediateCatchEvent("waitPayment", "payment");
        var subEnd = new EndEvent("subEnd");
        var paymentMessage = new MessageDefinition("payment", "paymentReceived", "orderId");
        var awaitPayment = new SubProcess("awaitPayment")
        {
            Activities = [subStart, waitPayment, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, waitPayment),
                new SequenceFlow("sf2", waitPayment, subEnd)
            ],
            Messages = [paymentMessage]
        };
        var start = new StartEvent("start");
        var timeout = new BoundaryTimerEvent("timeout", "awaitPayment", HalfHour);
        var timedOut = new TaskActivity("timedOut");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "interrupted-sub-with-message-catch",
            Activities = [start, awaitPayment, timeout, timedOut, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, awaitPayment),
                new SequenceFlow("f2", awaitPayment, end),
                new SequenceFlow("f3", timeout, timedOut)
            ],
            Messages = [paymentMessage]
        };
        var first = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await first.StartWorkflow(workflow, variables: OrderVariables(orderId));
        var running = await WaitForCondition(first.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("waitPayment"));
        var host = running.ActiveActivities.Single(a => a.ActivityId == "awaitPayment");

        // Act — the timeout interrupts the sub-process; a retry for the same order waits for the payment
        await first.HandleTimerFired("timeout", host.ActivityInstanceId);
        await WaitForCondition(first.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("timedOut"));
        var retry = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await retry.StartWorkflow(workflow with { WorkflowId = "interrupted-sub-with-message-catch-retry" },
            variables: OrderVariables(orderId));
        var retryRunning = await WaitForCondition(retry.GetPrimaryKey(),
            s => s.ActiveActivityIds.Contains("waitPayment") || s.CompletedActivityIds.Contains("waitPayment") || s.IsFailed);
        Assert.IsFalse(retryRunning.IsFailed || retryRunning.CompletedActivities.Any(a => a.ActivityId == "waitPayment" && a.ErrorState is not null),
            "the retry's catch must be able to subscribe to the released key");
        Assert.IsTrue(await DeliverMessageWhenSubscribed("paymentReceived", orderId));

        // Assert — the payment reaches the retry, not the cancelled catch
        var retryDone = await WaitForCondition(retry.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(retryDone.IsFailed);
        CollectionAssert.Contains(retryDone.CompletedActivityIds, "end");
        var firstAfter = (await QueryService.GetStateSnapshot(first.GetPrimaryKey()))!;
        Assert.IsTrue(firstAfter.ActiveActivityIds.Contains("timedOut"), "the interrupted instance is unaffected");
        Assert.IsFalse(firstAfter.CompletedActivities.Any(a => a.ActivityId == "waitPayment" && !a.IsCancelled));
    }

    // --- Call activity interrupted by a timer (#847, #782) ---

    [TestMethod]
    public async Task InterruptedCallActivity_CancelsTheChild_AndALateChildCompletionIsIgnored()
    {
        // Arrange — child: start → childTask → end; parent: start → call (timer → timedOut) → end
        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "timed-child",
            Activities = [new StartEvent("childStart"), new TaskActivity("childTask"), new EndEvent("childEnd")],
            SequenceFlows = []
        };
        childWorkflow = childWorkflow with
        {
            SequenceFlows =
            [
                new SequenceFlow("cs1", childWorkflow.Activities[0], childWorkflow.Activities[1]),
                new SequenceFlow("cs2", childWorkflow.Activities[1], childWorkflow.Activities[2])
            ]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("timed-child").DeployVersion(childWorkflow, "<xml/>");
        var start = new StartEvent("start");
        var call = new CallActivity("call", "timed-child", [], []);
        var timeout = new BoundaryTimerEvent("callTimeout", "call", HalfHour);
        var timedOut = new TaskActivity("timedOut");
        var end = new EndEvent("end");
        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "timed-parent",
            Activities = [start, call, timeout, timedOut, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, call),
                new SequenceFlow("f2", call, end),
                new SequenceFlow("f3", timeout, timedOut)
            ]
        };
        var parent = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var parentId = parent.GetPrimaryKey();
        await parent.StartWorkflow(parentWorkflow);
        var running = await WaitForCondition(parentId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "call" && a.ChildWorkflowInstanceId is not null));
        var callEntry = running.ActiveActivities.Single(a => a.ActivityId == "call");
        var childId = callEntry.ChildWorkflowInstanceId!.Value;
        await WaitForCondition(childId, s => s.ActiveActivityIds.Contains("childTask"));

        // Act — the timer interrupts the call activity
        await parent.HandleTimerFired("callTimeout", callEntry.ActivityInstanceId);

        // Assert — the child is stopped
        await WaitForCondition(parentId, s => s.ActiveActivityIds.Contains("timedOut"));
        var child = await WaitForCondition(childId, s => s.ActiveActivities.Count == 0);
        Assert.IsTrue(child.IsCancelled || child.CompletedActivities.Any(a => a.ActivityId == "childTask" && a.IsCancelled),
            "the child of an interrupted call activity is cancelled");

        // Act — a late completion of the child's task (e.g. a worker that was already running)
        try
        {
            await Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childId)
                .CompleteActivity("childTask", new ExpandoObject());
        }
        catch (Exception)
        {
            // Rejecting the late completion is fine too.
        }

        // Assert — the parent stays on the boundary path
        var parentAfter = (await QueryService.GetStateSnapshot(parentId))!;
        Assert.IsTrue(parentAfter.ActiveActivityIds.Contains("timedOut"));
        CollectionAssert.DoesNotContain(parentAfter.CompletedActivityIds, "end");
        Assert.IsFalse(parentAfter.CompletedActivities.Any(a => a.ActivityId == "call" && !a.IsCancelled));
    }

    // --- User tasks leave the task list when their scope is cancelled ---

    [TestMethod]
    public async Task UserTaskInsideAnInterruptedSubProcess_LeavesTheTaskList()
    {
        // Arrange — start → review(sub: approve user task) with a timer boundary → escalated
        var subStart = new StartEvent("subStart");
        var approve = new UserTask("approve", "john", [], [], null);
        var subEnd = new EndEvent("subEnd");
        var review = new SubProcess("review")
        {
            Activities = [subStart, approve, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, approve),
                new SequenceFlow("sf2", approve, subEnd)
            ]
        };
        var start = new StartEvent("start");
        var timeout = new BoundaryTimerEvent("timeout", "review", HalfHour);
        var escalated = new TaskActivity("escalated");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "interrupted-sub-with-user-task",
            Activities = [start, review, timeout, escalated, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, review),
                new SequenceFlow("f2", review, end),
                new SequenceFlow("f3", timeout, escalated)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("approve"));
        var userTaskId = running.ActiveActivities.Single(a => a.ActivityId == "approve").ActivityInstanceId;
        Assert.AreEqual(1, (await QueryService.GetActiveUserTasksForWorkflow(instanceId)).Count);
        var host = running.ActiveActivities.Single(a => a.ActivityId == "review");

        // Act
        await instance.HandleTimerFired("timeout", host.ActivityInstanceId);

        // Assert
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("escalated"));
        await AssertUserTaskCancelled(instanceId, userTaskId);
    }

    [TestMethod]
    public async Task UserTaskInterruptedByAnEventSubProcess_LeavesTheTaskList()
    {
        // Arrange — start → approve(user task) → end; interrupting message ESP "cancelOrder/<orderId>"
        var orderId = $"order-{Guid.NewGuid():N}";
        var msgStart = new MessageStartEvent("cancelStart", "cancelMsg");
        var handlerEnd = new EndEvent("cancelEnd");
        var esp = new EventSubProcess("onCancel")
        {
            Activities = [msgStart, handlerEnd],
            SequenceFlows = [new SequenceFlow("esp1", msgStart, handlerEnd)],
            IsInterrupting = true
        };
        var start = new StartEvent("start");
        var approve = new UserTask("approve", "john", [], [], null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "esp-cancels-user-task",
            Activities = [start, approve, end, esp],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, approve),
                new SequenceFlow("f2", approve, end)
            ],
            Messages = [new MessageDefinition("cancelMsg", "cancelOrder", "orderId")]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow, variables: OrderVariables(orderId));
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("approve"));
        var userTaskId = running.ActiveActivities.Single(a => a.ActivityId == "approve").ActivityInstanceId;

        // Act
        Assert.IsTrue(await DeliverMessageWhenSubscribed("cancelOrder", orderId));

        // Assert
        var done = await WaitForCondition(instanceId, s => s.IsCompleted);
        CollectionAssert.Contains(done.CompletedActivityIds, "cancelEnd");
        await AssertUserTaskCancelled(instanceId, userTaskId);
    }

    private async Task AssertUserTaskCancelled(Guid instanceId, Guid userTaskId)
    {
        var state = await Cluster.GrainFactory.GetGrain<IUserTaskGrain>(userTaskId).GetState();
        Assert.IsNotNull(state);
        Assert.AreEqual(Domain.States.UserTaskLifecycleState.Cancelled, state.TaskState,
            "an interrupted user task is cancelled, not completed");
        Assert.AreEqual(0, (await QueryService.GetActiveUserTasksForWorkflow(instanceId)).Count,
            "a cancelled user task is not in the instance's task list");
        var pending = await QueryService.GetPendingUserTasks("john", null, new PageRequest(1, 100));
        Assert.IsFalse(pending.Items.Any(t => t.ActivityInstanceId == userTaskId), "a cancelled user task is not pending");
    }

    private static ExpandoObject OrderVariables(string orderId)
    {
        dynamic vars = new ExpandoObject();
        vars.orderId = orderId;
        return vars;
    }
}
