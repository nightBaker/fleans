using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/20-complex-gateway/test-plan.md (scenario 20b)
[TestClass]
[TestCategory("E2E")]
public class ComplexGatewayTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task JoinWithActivationCondition_FiresOnFirstToken_DiscardsSecond()
    {
        var xml = BpmnFixtureLoader.Load("20-complex-gateway", "join-activation-condition.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        // Both branches run before reaching the join; the join's activationCondition
        // (_context._nroftoken >= 1) fires on the first token and the second is discarded.
        state.AssertCompletedActivities(
            "start", "fork", "fastTask", "slowTask", "join", "afterJoin", "end");
        state.AssertVariableEquals("joined", "True");
        Assert.IsEmpty(state.ActiveActivityIds);
    }
}
