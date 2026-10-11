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

    // #762: the fixture's `seed` script used `new[] { 1, 2, 3 }`, which DynamicExpresso cannot
    // parse, so `seed` failed and, with no error handler, the instance was left with no
    // active activities while neither completed nor failed. The fixture now uses
    // `new List<object> { ... }`, and the engine fails such instances visibly (IsFailed).
    [TestMethod]
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
        Assert.IsFalse(state.IsFailed);
        state.AssertCompletedActivities("start", "seed", "ct1", "iterate", "end");
        // The fleans:-namespaced multi-instance attributes were honoured: one iteration per item.
        Assert.IsTrue(state.TryGetVariable("doubled_results", out var doubled),
            "fleans:outputCollection should produce 'doubled_results'.");
        foreach (var expected in new[] { "2", "4", "6" })
            StringAssert.Contains(doubled, expected);
    }
}
