using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class CallActivityTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithMappings()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"">
      <extensionElements>
        <inputMapping source=""orderId"" target=""orderId"" />
        <inputMapping source=""amount"" target=""paymentAmount"" />
        <outputMapping source=""transactionId"" target=""txId"" />
      </extensionElements>
    </callActivity>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
        Assert.IsNotNull(callActivity);
        Assert.AreEqual("call1", callActivity.ActivityId);
        Assert.AreEqual("childProcess", callActivity.CalledProcessKey);
        Assert.AreEqual(2, callActivity.InputMappings.Count);
        Assert.AreEqual("orderId", callActivity.InputMappings[0].Source);
        Assert.AreEqual("orderId", callActivity.InputMappings[0].Target);
        Assert.AreEqual("amount", callActivity.InputMappings[1].Source);
        Assert.AreEqual("paymentAmount", callActivity.InputMappings[1].Target);
        Assert.AreEqual(1, callActivity.OutputMappings.Count);
        Assert.AreEqual("transactionId", callActivity.OutputMappings[0].Source);
        Assert.AreEqual("txId", callActivity.OutputMappings[0].Target);
        // Default propagation flags
        Assert.IsTrue(callActivity.PropagateAllParentVariables);
        Assert.IsTrue(callActivity.PropagateAllChildVariables);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithNoMappings()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
        Assert.IsNotNull(callActivity);
        Assert.AreEqual("childProcess", callActivity.CalledProcessKey);
        Assert.AreEqual(0, callActivity.InputMappings.Count);
        Assert.AreEqual(0, callActivity.OutputMappings.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithPropagationFlags()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" propagateAllParentVariables=""false"" propagateAllChildVariables=""false"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
        Assert.IsNotNull(callActivity);
        Assert.IsFalse(callActivity.PropagateAllParentVariables);
        Assert.IsFalse(callActivity.PropagateAllChildVariables);
    }
}
