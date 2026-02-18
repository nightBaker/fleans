using Fleans.Domain.Sequences;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class SequenceFlowTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseConditionalSequenceFlow_WithCondition()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow8", "flow1", "${x > 5}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.x > 5", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldConvertBpmnCondition_FromDollarNotation_ToContextNotation()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow9", "flow1", "${amount > 100 && status == 'active'}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.amount > 100 && _context.status == 'active'", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseRegularSequenceFlow_WithoutCondition()
    {
        // Arrange
        var bpmnXml = CreateSimpleBpmnXml("workflow10", "start", "end", "flow1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var flow = workflow.SequenceFlows.FirstOrDefault();
        Assert.IsNotNull(flow);
        Assert.IsInstanceOfType(flow, typeof(SequenceFlow));
        Assert.IsNotInstanceOfType(flow, typeof(ConditionalSequenceFlow));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleEmptyConditionExpression()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow13", "flow1", "");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleCondition_WithMultipleVariables()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow14", "flow1", "${x > y && z == 10}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.x > _context.y && _context.z == 10", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldPreserveCondition_WithoutDollarNotation()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow15", "flow1", "_context.x > 5");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.x > 5", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldSkipSequenceFlows_WithMissingSourceOrTarget()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithInvalidFlow("workflow11");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        // Should only have valid flows, not the one with missing target
        Assert.IsTrue(workflow.SequenceFlows.Count >= 1);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleMultipleConditionalFlows_FromSameGateway()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithMultipleConditionalFlows("workflow12", "gateway1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlows = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().ToList();
        Assert.IsTrue(conditionalFlows.Count >= 2);
        Assert.IsTrue(conditionalFlows.All(f => f.Source.ActivityId == "gateway1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseDefaultFlow_OnExclusiveGateway()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-default"">
    <startEvent id=""start"" />
    <exclusiveGateway id=""gw1"" default=""flowDefault"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <endEvent id=""endDefault"" />
    <sequenceFlow id=""flow0"" sourceRef=""start"" targetRef=""gw1"" />
    <sequenceFlow id=""flow1"" sourceRef=""gw1"" targetRef=""end1"">
      <conditionExpression>${x > 10}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flow2"" sourceRef=""gw1"" targetRef=""end2"">
      <conditionExpression>${x > 5}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flowDefault"" sourceRef=""gw1"" targetRef=""endDefault"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var defaultFlow = workflow.SequenceFlows.OfType<DefaultSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(defaultFlow, "Should have a DefaultSequenceFlow");
        Assert.AreEqual("flowDefault", defaultFlow.SequenceFlowId);
        Assert.AreEqual("gw1", defaultFlow.Source.ActivityId);
        Assert.AreEqual("endDefault", defaultFlow.Target.ActivityId);

        // Conditional flows should still be ConditionalSequenceFlow
        var conditionalFlows = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().ToList();
        Assert.AreEqual(2, conditionalFlows.Count);
    }
}
