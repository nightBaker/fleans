using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/37-custom-task-framework/test-plan.md (Scenario 1 only).
//
// Scenario 1 — unregistered plugin: the workflow has <serviceTask type="stub-task">
// and no plugin handler is registered for "stub-task", so the activity stays Active
// indefinitely until complete-activity is called manually. Scenario 2 (registered
// plugin auto-completes) needs runtime-pluggable plugin registration in the
// Aspire-hosted Api process — out of scope here; Plan 39 (RestCaller) covers the
// registered-handler happy path against an actually-registered plugin.
[TestClass]
[TestCategory("E2E")]
public class CustomTaskFrameworkTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task UnregisteredPlugin_ActivityStaysActive_ManualCompleteUnblocks()
    {
        var xml = BpmnFixtureLoader.Load("37-custom-task-framework", "stub-custom-task.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Wait until the unregistered serviceTask is sitting in Active.
        var waiting = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("ct1"),
            timeout: TimeSpan.FromSeconds(10));

        Assert.IsFalse(waiting.IsCompleted, "Workflow must not auto-complete an unregistered plugin task.");

        // Manual complete-activity is the only way forward with no plugin registered.
        using (var resp = await ApiClient.CompleteActivityAsync(
            started.WorkflowInstanceId,
            "ct1",
            new Dictionary<string, object> { ["echo"] = "manual" }))
        {
            Assert.IsTrue(resp.IsSuccessStatusCode,
                $"complete-activity should succeed; got {resp.StatusCode}.");
        }

        var final = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        final.AssertCompletedActivities("start", "ct1", "end");
        final.AssertVariableEquals("echo", "manual");
    }
}
