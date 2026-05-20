using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/16-message-start-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class MessageStartEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task MessageStartEvent_CreatesInstanceFromIncomingMessage()
    {
        var xml = BpmnFixtureLoader.Load("16-message-start-event", "message-start-event.bpmn");
        await ApiClient.DeployAsync(xml);

        var delivery1 = await ApiClient.SendMessageAsync(
            "orderReceived",
            variables: new Dictionary<string, object?> { ["orderId"] = "ORD-001" });
        Assert.IsTrue(delivery1.Delivered, "First message should be delivered.");
        Assert.IsNotNull(delivery1.WorkflowInstanceIds);
        Assert.HasCount(1, delivery1.WorkflowInstanceIds);

        var firstId = delivery1.WorkflowInstanceIds[0];
        var firstState = await ApiClient.WaitForCompletionAsync(firstId);
        firstState.AssertCompletedActivities("msgStart", "processOrder", "end");

        var delivery2 = await ApiClient.SendMessageAsync(
            "orderReceived",
            variables: new Dictionary<string, object?> { ["orderId"] = "ORD-002" });
        Assert.IsTrue(delivery2.Delivered);
        Assert.IsNotNull(delivery2.WorkflowInstanceIds);
        Assert.HasCount(1, delivery2.WorkflowInstanceIds);
        Assert.AreNotEqual(firstId, delivery2.WorkflowInstanceIds[0],
            "Each message should create a distinct instance.");
    }

    [TestMethod]
    public async Task UnknownMessageName_Returns404()
    {
        var xml = BpmnFixtureLoader.Load("16-message-start-event", "message-start-event.bpmn");
        await ApiClient.DeployAsync(xml);

        using var response = await ApiClient.SendMessageRawAsync("unknownMessage");
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
