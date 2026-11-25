using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/51-end-event-variants/test-plan.md
//
// The manual plan #522 is editor-UI focused (clicking nodes to inspect property panel labels),
// which we don't drive yet. These specs verify what each end-event variant *does* at run time.
// The earlier "…_DeploysAndCompletes" specs only checked IsCompleted, which a plain end event
// also satisfies, so they stayed green while the variants ran as plain end events (#843).
// Each spec now asserts the variant's own semantics and is ignored until #843 lands.
[TestClass]
[TestCategory("E2E")]
public class EndEventVariantsTests : WorkflowE2ETestBase
{
    private const string Plan = "51-end-event-variants";

    [TestMethod]
    [Ignore("Blocked by #843: an error end event is parsed as a plain end event, so the sub-process completes normally and its error boundary never fires.")]
    public async Task ErrorEnd_InsideASubProcess_FiresTheErrorBoundary()
    {
        // Arrange
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "error-end-in-subprocess.bpmn"));

        // Act
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        // Assert
        CollectionAssert.Contains(state.CompletedActivityIds, "handled_end");
        CollectionAssert.DoesNotContain(state.CompletedActivityIds, "normal_end");
    }

    [TestMethod]
    [Ignore("Blocked by #843: an error end event is parsed as a plain end event, so an uncaught one completes the instance instead of failing it.")]
    public async Task ErrorEnd_Uncaught_FailsTheInstance()
    {
        // Arrange
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "error-end.bpmn"));

        // Act
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForStateAsync(started.WorkflowInstanceId,
            s => s.IsCompleted || s.IsFailed, timeout: TimeSpan.FromSeconds(10));

        // Assert
        Assert.IsTrue(state.IsFailed, "an uncaught error end event fails the instance");
    }

    [TestMethod]
    [Ignore("Blocked by #843: a terminate end event is parsed as a plain end event, so the parallel branch keeps running.")]
    public async Task TerminateEnd_WithAnActiveParallelBranch_EndsTheInstance()
    {
        // Arrange
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "terminate-end-parallel.bpmn"));

        // Act
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(10));

        // Assert
        CollectionAssert.Contains(state.CompletedActivityIds, "terminate_end");
        CollectionAssert.DoesNotContain(state.CompletedActivityIds, "normal_end");
        Assert.AreEqual(0, state.ActiveActivityIds.Count, "the waiting branch is cancelled");
    }

    [TestMethod]
    [Ignore("Blocked by #843: a signal end event is parsed as a plain end event and never broadcasts.")]
    public async Task SignalEnd_BroadcastsItsSignal()
    {
        // Arrange — a catcher waits for "processCompleted"
        var catcher = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "signal-end-catcher.bpmn"));
        var waiting = await ApiClient.StartAsync(catcher.ProcessDefinitionKey);
        await ApiClient.WaitForStateAsync(waiting.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("wait_signal"));
        var thrower = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Plan, "signal-end.bpmn"));

        // Act
        var started = await ApiClient.StartAsync(thrower.ProcessDefinitionKey);
        await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        // Assert
        var caught = await ApiClient.WaitForCompletionAsync(waiting.WorkflowInstanceId);
        CollectionAssert.Contains(caught.CompletedActivityIds, "end");
    }

    [TestMethod]
    [Ignore("Blocked by #843: a message end event is parsed as a plain end event and sends nothing; until it is implemented it should be rejected at deploy.")]
    public async Task MessageEnd_IsRejectedAtDeploy_UntilItSendsItsMessage()
    {
        // Act
        var response = await ApiClient.DeployRawAsync(BpmnFixtureLoader.Load(Plan, "message-end.bpmn"));

        // Assert
        Assert.IsFalse(response.IsSuccessStatusCode, "a message end event must not deploy as a silent plain end event");
    }
}
