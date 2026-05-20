using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/51-end-event-variants/test-plan.md
//
// The manual plan #522 is editor-UI focused (clicking nodes to inspect property panel
// labels), which we don't drive yet. These specs instead verify the engine-level
// behaviour of each end event variant: deploying a fixture with each variant and
// confirming the workflow runs through it.
[TestClass]
[TestCategory("E2E")]
public class EndEventVariantsTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task SignalEnd_DeploysAndCompletes()
    {
        var xml = BpmnFixtureLoader.Load("51-end-event-variants", "signal-end.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        Assert.IsTrue(state.IsCompleted);
    }

    [TestMethod]
    public async Task MessageEnd_DeploysAndCompletes()
    {
        var xml = BpmnFixtureLoader.Load("51-end-event-variants", "message-end.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        Assert.IsTrue(state.IsCompleted);
    }

    [TestMethod]
    public async Task ErrorEnd_DeploysAndCompletes()
    {
        var xml = BpmnFixtureLoader.Load("51-end-event-variants", "error-end.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(10));
        Assert.IsTrue(state.IsCompleted);
    }
}
