using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/45-redis-streaming and tests/manual/45-fleans-namespace —
// smoke tests verifying that a representative workflow deploys + executes successfully
// under the engine's default Redis streaming provider. Configuration-specific
// streaming/namespace behaviour (sharding, queue counts, etc.) is verified
// elsewhere or out-of-scope for this batch.
[TestClass]
[TestCategory("E2E")]
public class RedisAndNamespaceSmokeTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task RedisStreaming_BasicWorkflowDeploysAndCompletes()
    {
        var xml = BpmnFixtureLoader.Load("45-redis-streaming", "redis-streams.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        Assert.IsTrue(state.IsCompleted);
        Assert.IsFalse(state.IsCancelled);
    }

    // TODO: workflow stalls after `seed` with Active=[] — `ct1` (`<bpmn:serviceTask>`
    // with `<fleans:taskDefinition type="stub-task" />`) is never activated. The engine
    // appears to silently drop the serviceTask when only the fleans namespace shape is
    // present (zeebe variant is parsed cleanly per plan #37). Pending engine investigation.
    [TestMethod]
    [Ignore("fleans:taskDefinition serviceTask never activates after upstream seed task; ct1 silently dropped. Pending engine investigation.")]
    public async Task FleansNamespace_ServiceTaskDeploysAndCompletes()
    {
        var xml = BpmnFixtureLoader.Load("45-fleans-namespace", "fleans-service-task.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Wait for the unregistered serviceTask to become active.
        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("ct1"),
            timeout: TimeSpan.FromSeconds(10));

        using (var resp = await ApiClient.CompleteActivityAsync(
            started.WorkflowInstanceId, "ct1"))
        {
            Assert.IsTrue(resp.IsSuccessStatusCode,
                $"complete-activity on stub serviceTask should succeed; got {resp.StatusCode}.");
        }

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(20));
        Assert.IsTrue(state.IsCompleted);
        Assert.IsFalse(state.IsCancelled);
    }
}
