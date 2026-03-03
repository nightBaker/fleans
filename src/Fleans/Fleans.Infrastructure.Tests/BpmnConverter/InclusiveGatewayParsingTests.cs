using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class InclusiveGatewayParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ShouldParseInclusiveGateway_AsFork()
    {
        // Arrange — 1 incoming, 2 outgoing => fork
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <inclusiveGateway id=""ig1"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""s1"" sourceRef=""start"" targetRef=""ig1"" />
    <sequenceFlow id=""s2"" sourceRef=""ig1"" targetRef=""end1"">
      <conditionExpression>true</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""s3"" sourceRef=""ig1"" targetRef=""end2"">
      <conditionExpression>true</conditionExpression>
    </sequenceFlow>
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<InclusiveGateway>().FirstOrDefault(g => g.ActivityId == "ig1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork);
    }

    [TestMethod]
    public async Task ShouldParseInclusiveGateway_AsJoin()
    {
        // Arrange — 2 incoming, 1 outgoing => join
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <task id=""task1"" />
    <task id=""task2"" />
    <inclusiveGateway id=""ig1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""s1"" sourceRef=""task1"" targetRef=""ig1"" />
    <sequenceFlow id=""s2"" sourceRef=""task2"" targetRef=""ig1"" />
    <sequenceFlow id=""s3"" sourceRef=""ig1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<InclusiveGateway>().FirstOrDefault(g => g.ActivityId == "ig1");
        Assert.IsNotNull(gateway);
        Assert.IsFalse(gateway.IsFork);
    }

    [TestMethod]
    public async Task ShouldParseInclusiveGateway_DefaultAttribute()
    {
        // Arrange — default attribute on gateway should produce a DefaultSequenceFlow
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <inclusiveGateway id=""ig1"" default=""s3"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""s1"" sourceRef=""start"" targetRef=""ig1"" />
    <sequenceFlow id=""s2"" sourceRef=""ig1"" targetRef=""end1"">
      <conditionExpression>true</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""s3"" sourceRef=""ig1"" targetRef=""end2"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<InclusiveGateway>().FirstOrDefault(g => g.ActivityId == "ig1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork);

        var defaultFlow = workflow.SequenceFlows.OfType<DefaultSequenceFlow>().FirstOrDefault(sf => sf.SequenceFlowId == "s3");
        Assert.IsNotNull(defaultFlow, "Should have a DefaultSequenceFlow for s3");
        Assert.AreEqual("ig1", defaultFlow.Source.ActivityId);
        Assert.AreEqual("end2", defaultFlow.Target.ActivityId);
    }
}
