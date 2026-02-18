using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class BoundaryErrorEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseBoundaryErrorEvent_WithErrorCode()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""call1"">
      <errorEventDefinition errorRef=""PaymentFailed"" />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var boundaryEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
        Assert.IsNotNull(boundaryEvent);
        Assert.AreEqual("err1", boundaryEvent.ActivityId);
        Assert.AreEqual("call1", boundaryEvent.AttachedToActivityId);
        Assert.AreEqual("PaymentFailed", boundaryEvent.ErrorCode);

        // Sequence flow from boundary event to errorEnd
        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == boundaryEvent);
        Assert.IsNotNull(flow);
        Assert.AreEqual("errorEnd", flow.Target.ActivityId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseBoundaryErrorEvent_CatchAll_WhenNoErrorRef()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""call1"">
      <errorEventDefinition />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var boundaryEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
        Assert.IsNotNull(boundaryEvent);
        Assert.IsNull(boundaryEvent.ErrorCode);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldStillParseBoundaryErrorEvent_WhenErrorDefinitionPresent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""error-workflow"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""task1"">
      <errorEventDefinition errorRef=""500"" />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var errorEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
        Assert.IsNotNull(errorEvent);
        Assert.AreEqual("err1", errorEvent.ActivityId);
        Assert.AreEqual("task1", errorEvent.AttachedToActivityId);
        Assert.AreEqual("500", errorEvent.ErrorCode);
    }
}
