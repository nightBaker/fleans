using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class ComplexGatewayParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ShouldParseComplexGateway_AsFork_WhenMoreOutgoingThanIncoming()
    {
        // Arrange — 1 incoming, 2 outgoing => fork
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <complexGateway id=""cg1"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""s1"" sourceRef=""start"" targetRef=""cg1"" />
    <sequenceFlow id=""s2"" sourceRef=""cg1"" targetRef=""end1"">
      <conditionExpression>x &gt; 0</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""s3"" sourceRef=""cg1"" targetRef=""end2"">
      <conditionExpression>x &lt; 10</conditionExpression>
    </sequenceFlow>
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ComplexGateway>().FirstOrDefault(g => g.ActivityId == "cg1");
        Assert.IsNotNull(gateway, "Should have a ComplexGateway activity");
        Assert.IsTrue(gateway.IsFork);
        Assert.IsNull(gateway.ActivationCondition, "Fork gateways should not have an ActivationCondition");
    }

    [TestMethod]
    public async Task ShouldParseComplexGateway_AsJoin_WhenMoreIncomingThanOutgoing()
    {
        // Arrange — 2 incoming, 1 outgoing => join
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <scriptTask id=""task1"" />
    <scriptTask id=""task2"" />
    <complexGateway id=""cg1"" activationCondition=""_context._nroftoken &gt;= 1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""s1"" sourceRef=""task1"" targetRef=""cg1"" />
    <sequenceFlow id=""s2"" sourceRef=""task2"" targetRef=""cg1"" />
    <sequenceFlow id=""s3"" sourceRef=""cg1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ComplexGateway>().FirstOrDefault(g => g.ActivityId == "cg1");
        Assert.IsNotNull(gateway, "Should have a ComplexGateway activity");
        Assert.IsFalse(gateway.IsFork);
        Assert.AreEqual("_context._nroftoken >= 1", gateway.ActivationCondition);
    }

    [TestMethod]
    public async Task ShouldParseComplexGateway_AsJoin_WithoutActivationCondition()
    {
        // Arrange — join without activationCondition => behaves like parallel gateway join
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <scriptTask id=""task1"" />
    <scriptTask id=""task2"" />
    <complexGateway id=""cg1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""s1"" sourceRef=""task1"" targetRef=""cg1"" />
    <sequenceFlow id=""s2"" sourceRef=""task2"" targetRef=""cg1"" />
    <sequenceFlow id=""s3"" sourceRef=""cg1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ComplexGateway>().FirstOrDefault(g => g.ActivityId == "cg1");
        Assert.IsNotNull(gateway);
        Assert.IsFalse(gateway.IsFork);
        Assert.IsNull(gateway.ActivationCondition, "No activationCondition attribute => null");
    }

    [TestMethod]
    public async Task ShouldParseComplexGateway_DefaultFlow()
    {
        // Arrange — complexGateway with default attribute => DefaultSequenceFlow
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <complexGateway id=""cg1"" default=""s3"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""s1"" sourceRef=""start"" targetRef=""cg1"" />
    <sequenceFlow id=""s2"" sourceRef=""cg1"" targetRef=""end1"">
      <conditionExpression>x &gt; 0</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""s3"" sourceRef=""cg1"" targetRef=""end2"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ComplexGateway>().FirstOrDefault(g => g.ActivityId == "cg1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork);

        var defaultFlow = workflow.SequenceFlows.OfType<DefaultSequenceFlow>().FirstOrDefault(sf => sf.SequenceFlowId == "s3");
        Assert.IsNotNull(defaultFlow, "Should produce a DefaultSequenceFlow for s3");
        Assert.AreEqual("cg1", defaultFlow.Source.ActivityId);
    }

    [TestMethod]
    public async Task ShouldThrow_WhenComplexGateway_HasEqualIncomingAndOutgoing_BothGreaterThanOne()
    {
        // Arrange — N:N where N > 1 is a mixed gateway — not supported
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <scriptTask id=""task1"" />
    <scriptTask id=""task2"" />
    <complexGateway id=""cg1"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""s1"" sourceRef=""task1"" targetRef=""cg1"" />
    <sequenceFlow id=""s2"" sourceRef=""task2"" targetRef=""cg1"" />
    <sequenceFlow id=""s3"" sourceRef=""cg1"" targetRef=""end1"" />
    <sequenceFlow id=""s4"" sourceRef=""cg1"" targetRef=""end2"" />
  </process>
</definitions>";

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml))));
    }
}
