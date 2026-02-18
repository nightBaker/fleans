using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class GatewayTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseExclusiveGateway()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithExclusiveGateway("workflow5", "gateway1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is ExclusiveGateway && a.ActivityId == "gateway1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseParallelGateway_AsFork_WhenMoreOutgoingThanIncoming()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithParallelGateway("workflow6", "fork1", isFork: true);

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ParallelGateway>().FirstOrDefault(g => g.ActivityId == "fork1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseParallelGateway_AsJoin_WhenMoreIncomingThanOutgoing()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithParallelGateway("workflow7", "join1", isFork: false);

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ParallelGateway>().FirstOrDefault(g => g.ActivityId == "join1");
        Assert.IsNotNull(gateway);
        Assert.IsFalse(gateway.IsFork);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseParallelGateway_AsFork_WhenEqualSingleIncomingAndOutgoing()
    {
        // Arrange — 1 incoming, 1 outgoing = pass-through, treated as fork
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-passthrough"">
    <startEvent id=""start"" />
    <parallelGateway id=""gw1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""gw1"" />
    <sequenceFlow id=""flow2"" sourceRef=""gw1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ParallelGateway>().FirstOrDefault(g => g.ActivityId == "gw1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork, "A 1:1 parallel gateway should be treated as a fork (pass-through)");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrow_WhenParallelGatewayHasEqualMultipleIncomingAndOutgoing()
    {
        // Arrange — 2 incoming, 2 outgoing = mixed gateway, not supported
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-mixed"">
    <task id=""task1"" />
    <task id=""task2"" />
    <parallelGateway id=""gw1"" />
    <task id=""task3"" />
    <task id=""task4"" />
    <sequenceFlow id=""flow1"" sourceRef=""task1"" targetRef=""gw1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task2"" targetRef=""gw1"" />
    <sequenceFlow id=""flow3"" sourceRef=""gw1"" targetRef=""task3"" />
    <sequenceFlow id=""flow4"" sourceRef=""gw1"" targetRef=""task4"" />
  </process>
</definitions>";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        });
    }
}
