using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/84-bpmn-combinations/test-plan.md
//
// Elements that are covered one by one elsewhere, combined: a loop through a waiting catch event,
// and interrupting timers on scopes that hold waiting elements (a user task, a child process).
[TestClass]
[TestCategory("E2E")]
public class BpmnCombinationTests : WorkflowE2ETestBase
{
    private const string Plan = "84-bpmn-combinations";

    [TestMethod]
    public async Task MessageCatchLoop_EachPassReceivesOneMessage()
    {
        // Arrange
        var orderId = $"loop-{Guid.NewGuid():N}";
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "message-catch-loop.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["orderId"] = orderId });

        for (var pass = 1; pass <= 3; pass++)
        {
            // Act — one message per pass, sent once the pass's catch is waiting
            var expectedCompletions = pass - 1;
            await ApiClient.WaitForStateAsync(started.WorkflowInstanceId,
                s => s.ActiveActivityIds.Contains("wait_payment")
                     && s.CompletedActivityIds.Count(id => id == "wait_payment") == expectedCompletions);
            var delivery = await SendWhenSubscribed("loopPayment", orderId);

            // Assert
            Assert.IsTrue(delivery, $"pass {pass}: the message reaches the waiting catch");
        }

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        Assert.AreEqual(3, state.CompletedActivityIds.Count(id => id == "wait_payment"));
        CollectionAssert.Contains(state.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task SubProcessTimeout_RemovesItsUserTaskFromTheTaskList()
    {
        // Arrange
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "subprocess-user-task-timeout.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var running = await ApiClient.WaitForStateAsync(started.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("approve"));
        var taskId = running.ActiveActivities.Single(a => a.ActivityId == "approve").ActivityInstanceId;
        await ApiClient.WaitForUserTaskAsync(taskId, t => t is not null);

        // Act — the 5 s timer interrupts the sub-process
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));

        // Assert
        CollectionAssert.Contains(state.CompletedActivityIds, "escalated_end");
        var pending = await ApiClient.GetPendingUserTasksAsync(assignee: "timeout-reviewer");
        Assert.IsFalse(pending.Items.Any(t => t.ActivityInstanceId == taskId), "the interrupted user task leaves the task list");
    }

    [TestMethod]
    public async Task CallActivityTimeout_CancelsTheChildInstance()
    {
        // Arrange
        await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "call-activity-timeout-child.bpmn"));
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "call-activity-timeout.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var running = await ApiClient.WaitForStateAsync(started.WorkflowInstanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "call_child" && a.ChildWorkflowInstanceId is not null));
        var childId = running.ActiveActivities.Single(a => a.ActivityId == "call_child").ChildWorkflowInstanceId!.Value;

        // Act — the 5 s timer interrupts the call activity
        var parent = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));

        // Assert
        CollectionAssert.Contains(parent.CompletedActivityIds, "timed_out");
        var child = await ApiClient.WaitForStateAsync(childId, s => s.ActiveActivityIds.Count == 0, timeout: TimeSpan.FromSeconds(10));
        Assert.IsTrue(child.IsCancelled || child.IsCompleted, "the child no longer runs");
        var pending = await ApiClient.GetPendingUserTasksAsync(assignee: "john");
        Assert.IsFalse(pending.Items.Any(t => t.WorkflowInstanceId == childId), "the child's user task leaves the task list");
    }

    private async Task<bool> SendWhenSubscribed(string messageName, string correlationKey)
    {
        // The catch subscribes as an effect of activating it, so retry until the key is taken.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var response = await ApiClient.SendMessageAsync(messageName, correlationKey);
            if (response.Delivered || DateTime.UtcNow >= deadline)
                return response.Delivered;
            await Task.Delay(250);
        }
    }
}
