using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class EscalationEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEscalationDefinitions()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <escalation id=""Escalation_1"" name=""PaymentOverdue"" escalationCode=""ESC_001"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        Assert.HasCount(1, workflow.Escalations);
        Assert.AreEqual("Escalation_1", workflow.Escalations[0].Id);
        Assert.AreEqual("ESC_001", workflow.Escalations[0].EscalationCode);
        Assert.AreEqual("PaymentOverdue", workflow.Escalations[0].Name);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEscalationEndEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <escalation id=""Escalation_1"" name=""PaymentOverdue"" escalationCode=""ESC_001"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <endEvent id=""escEnd"">
      <escalationEventDefinition escalationRef=""Escalation_1"" />
    </endEvent>
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""escEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var escEnd = workflow.Activities.OfType<EscalationEndEvent>().SingleOrDefault();
        Assert.IsNotNull(escEnd);
        Assert.AreEqual("escEnd", escEnd.ActivityId);
        Assert.AreEqual("ESC_001", escEnd.EscalationCode);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEscalationIntermediateThrowEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <escalation id=""Escalation_1"" name=""PaymentOverdue"" escalationCode=""ESC_001"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateThrowEvent id=""escThrow"">
      <escalationEventDefinition escalationRef=""Escalation_1"" />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""escThrow"" />
    <sequenceFlow id=""f2"" sourceRef=""escThrow"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var escThrow = workflow.Activities.OfType<EscalationIntermediateThrowEvent>().SingleOrDefault();
        Assert.IsNotNull(escThrow);
        Assert.AreEqual("escThrow", escThrow.ActivityId);
        Assert.AreEqual("ESC_001", escThrow.EscalationCode);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEscalationBoundaryEvent_Interrupting()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <escalation id=""Escalation_1"" name=""PaymentOverdue"" escalationCode=""ESC_001"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <subProcess id=""sub1"">
      <startEvent id=""subStart"" />
      <endEvent id=""subEnd"" />
      <sequenceFlow id=""sf1"" sourceRef=""subStart"" targetRef=""subEnd"" />
    </subProcess>
    <boundaryEvent id=""escBoundary"" attachedToRef=""sub1"" cancelActivity=""true"">
      <escalationEventDefinition escalationRef=""Escalation_1"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""escEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""sub1"" />
    <sequenceFlow id=""f2"" sourceRef=""sub1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""escBoundary"" targetRef=""escEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var escBoundary = workflow.Activities.OfType<EscalationBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(escBoundary);
        Assert.AreEqual("escBoundary", escBoundary.ActivityId);
        Assert.AreEqual("sub1", escBoundary.AttachedToActivityId);
        Assert.AreEqual("ESC_001", escBoundary.EscalationCode);
        Assert.IsTrue(escBoundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEscalationBoundaryEvent_NonInterrupting()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <escalation id=""Escalation_1"" name=""PaymentOverdue"" escalationCode=""ESC_001"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <subProcess id=""sub1"">
      <startEvent id=""subStart"" />
      <endEvent id=""subEnd"" />
      <sequenceFlow id=""sf1"" sourceRef=""subStart"" targetRef=""subEnd"" />
    </subProcess>
    <boundaryEvent id=""escBoundary"" attachedToRef=""sub1"" cancelActivity=""false"">
      <escalationEventDefinition escalationRef=""Escalation_1"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""escEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""sub1"" />
    <sequenceFlow id=""f2"" sourceRef=""sub1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""escBoundary"" targetRef=""escEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var escBoundary = workflow.Activities.OfType<EscalationBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(escBoundary);
        Assert.IsFalse(escBoundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEscalationBoundaryEvent_CatchAll_NoEscalationRef()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""start"" />
    <subProcess id=""sub1"">
      <startEvent id=""subStart"" />
      <endEvent id=""subEnd"" />
      <sequenceFlow id=""sf1"" sourceRef=""subStart"" targetRef=""subEnd"" />
    </subProcess>
    <boundaryEvent id=""escBoundary"" attachedToRef=""sub1"">
      <escalationEventDefinition />
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""escEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""sub1"" />
    <sequenceFlow id=""f2"" sourceRef=""sub1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""escBoundary"" targetRef=""escEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var escBoundary = workflow.Activities.OfType<EscalationBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(escBoundary);
        Assert.IsNull(escBoundary.EscalationCode);
    }
}
