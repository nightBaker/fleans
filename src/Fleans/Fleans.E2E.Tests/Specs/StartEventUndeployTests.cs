using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/18-start-event-undeploy/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class StartEventUndeployTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task DisableProcess_StopsStartEventListeners_EnableReregistersThem()
    {
        var xml = BpmnFixtureLoader.Load("18-start-event-undeploy", "signal-start-disable.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);

        // Initially the signal should start an instance.
        var first = await ApiClient.SendSignalAsync("test-disable-signal");
        Assert.IsNotNull(first.WorkflowInstanceIds);
        Assert.HasCount(1, first.WorkflowInstanceIds);

        // Disable.
        using (var disableResp = await ApiClient.DisableAsync(deployed.ProcessDefinitionKey))
        {
            Assert.IsTrue(disableResp.IsSuccessStatusCode,
                $"Disable should return success; got {disableResp.StatusCode}.");
        }

        // Signal must no longer create an instance (404 — no subscription).
        using (var blockedSignal = await ApiClient.SendSignalRawAsync("test-disable-signal"))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, blockedSignal.StatusCode);
        }

        // Re-enable.
        using (var enableResp = await ApiClient.EnableAsync(deployed.ProcessDefinitionKey))
        {
            Assert.IsTrue(enableResp.IsSuccessStatusCode);
        }

        // Signal should start instances again.
        var third = await ApiClient.SendSignalAsync("test-disable-signal");
        Assert.IsNotNull(third.WorkflowInstanceIds);
        Assert.HasCount(1, third.WorkflowInstanceIds);
    }
}
