using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-compensation-event/test-plan.md (Scenarios B + C — targeted throw, end event)
[TestClass]
[TestCategory("E2E")]
public class CompensationThrowAndEndEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task CompensationThrow_WithActivityRef_CompensatesOnlyTheTargetedActivity()
    {
        var xml = BpmnFixtureLoader.Load("24-compensation-event", "compensation-targeted.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("reserve_hotel", "book_flight", "cancel_flight", "compensate_flight", "end");
        state.AssertNotCompleted("cancel_hotel");
        state.AssertVariableEquals("flightStatus", "cancelled");
        AssertNoScopeHasValue(state, "hotelStatus", "cancelled");
        state.AssertVariableEquals("hotelStatus", "reserved");
    }

    [TestMethod]
    public async Task CompensationEndEvent_CompensatesAllCompletedActivities_AndEndsTheProcess()
    {
        var xml = BpmnFixtureLoader.Load("24-compensation-event", "compensation-end-event.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        Assert.IsFalse(state.IsCancelled, "Compensation end event must complete the process, not cancel it.");
        state.AssertCompletedActivities(
            "reserve_hotel", "book_flight", "cancel_flight", "cancel_hotel", "compensate_end");
        state.AssertVariableEquals("hotelStatus", "cancelled");
        state.AssertVariableEquals("flightStatus", "cancelled");

        // Reverse completion order: book_flight completed last, so its handler runs first.
        var cancelFlight = state.CompletedActivities.Single(a => a.ActivityId == "cancel_flight");
        var cancelHotel = state.CompletedActivities.Single(a => a.ActivityId == "cancel_hotel");
        Assert.IsNotNull(cancelFlight.CompletedAt);
        Assert.IsNotNull(cancelHotel.CompletedAt);
        Assert.IsLessThanOrEqualTo(cancelHotel.CompletedAt.Value, cancelFlight.CompletedAt.Value,
            "cancel_flight should complete before cancel_hotel (reverse completion order).");
    }

    private static void AssertNoScopeHasValue(
        Fleans.Application.QueryModels.InstanceStateSnapshot state, string name, string forbidden)
    {
        foreach (var scope in state.VariableStates)
        {
            if (scope.Variables.TryGetValue(name, out var v))
            {
                Assert.AreNotEqual(forbidden, v,
                    $"Variable '{name}' must not be '{forbidden}' in any scope — its activity was not targeted.");
            }
        }
    }
}
