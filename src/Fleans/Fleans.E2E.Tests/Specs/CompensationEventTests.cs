using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-compensation-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class CompensationEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task CompensationBroadcast_RunsHandlersInReverseCompletionOrder()
    {
        var xml = BpmnFixtureLoader.Load("24-compensation-event", "compensation-broadcast.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities(
            "reserve_hotel", "book_flight",
            "cancel_hotel", "cancel_flight",
            "compensate_all", "end");
        state.AssertVariableEquals("hotelStatus", "cancelled");
        state.AssertVariableEquals("flightStatus", "cancelled");
    }
}
