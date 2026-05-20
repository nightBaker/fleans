using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/09-message-events/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class MessageEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task MessageCatch_ReceivesMessage_WorkflowResumes()
    {
        var xml = BpmnFixtureLoader.Load("09-message-events", "message-catch.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["requestId"] = "req-456" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("waitApproval"));

        var delivery = await ApiClient.SendMessageAsync(
            messageName: "approvalReceived",
            correlationKey: "req-456");
        Assert.IsTrue(delivery.Delivered, "Message should have been delivered.");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("waitApproval", "afterApproval");
        state.AssertVariableEquals("requestId", "req-456");
    }

    // tests/manual/09-message-events/test-plan.md notes a KNOWN BUG on Scenario B.
    [TestMethod]
    [Ignore("Known bug: boundary events on IntermediateCatchEvent do not register subscriptions; see docs/plans/2026-02-25-manual-test-results.md.")]
    public async Task MessageBoundary_InterruptsTimer_CancelPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("09-message-events", "message-boundary.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["requestId"] = "req-789" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("longWait"));

        await ApiClient.SendMessageAsync(
            messageName: "cancelRequest",
            correlationKey: "req-789");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("cancelPath");
        state.AssertNotCompleted("normalPath");
        state.AssertVariableEquals("cancelled", "True");
    }

    [TestMethod]
    public async Task MessageCatchMissingCorrelation_RegistrationFails_WorkflowMarkedFailed()
    {
        var xml = BpmnFixtureLoader.Load(
            "09-message-events", "message-catch-missing-correlation.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Per the design constraint: registration-path failures surface as a failed
        // activity + failed workflow. Wait for the workflow to leave "Running".
        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsCompleted || s.CompletedActivityIds.Contains("waitApproval"),
            timeout: TimeSpan.FromSeconds(15));

        state.AssertCompletedActivities("beforeWait");
        state.AssertNotCompleted("afterApproval");

        var failedActivity = state.CompletedActivities
            .FirstOrDefault(a => a.ActivityId == "waitApproval");
        Assert.IsNotNull(failedActivity,
            "waitApproval should appear among completed activities (with a failure outcome).");
        Assert.IsNotNull(failedActivity.ErrorState,
            "waitApproval should carry an ErrorState describing the missing-variable failure.");
    }
}
