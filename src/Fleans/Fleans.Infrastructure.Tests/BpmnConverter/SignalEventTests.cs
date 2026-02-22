using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class SignalEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSignalIntermediateCatchEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <signal id=""Signal_1"" name=""orderApproved"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitSignal"">
      <signalEventDefinition signalRef=""Signal_1"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitSignal"" />
    <sequenceFlow id=""f2"" sourceRef=""waitSignal"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var signalCatch = workflow.Activities.OfType<SignalIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(signalCatch);
        Assert.AreEqual("waitSignal", signalCatch.ActivityId);
        Assert.AreEqual("Signal_1", signalCatch.SignalDefinitionId);

        Assert.AreEqual(1, workflow.Signals.Count);
        Assert.AreEqual("Signal_1", workflow.Signals[0].Id);
        Assert.AreEqual("orderApproved", workflow.Signals[0].Name);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSignalIntermediateThrowEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <signal id=""Signal_1"" name=""orderApproved"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateThrowEvent id=""emitSignal"">
      <signalEventDefinition signalRef=""Signal_1"" />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""emitSignal"" />
    <sequenceFlow id=""f2"" sourceRef=""emitSignal"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var signalThrow = workflow.Activities.OfType<SignalIntermediateThrowEvent>().SingleOrDefault();
        Assert.IsNotNull(signalThrow);
        Assert.AreEqual("emitSignal", signalThrow.ActivityId);
        Assert.AreEqual("Signal_1", signalThrow.SignalDefinitionId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSignalBoundaryEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <signal id=""Signal_1"" name=""cancelOrder"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <boundaryEvent id=""bsig1"" attachedToRef=""task1"">
      <signalEventDefinition signalRef=""Signal_1"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""sigEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bsig1"" targetRef=""sigEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var boundarySignal = workflow.Activities.OfType<SignalBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(boundarySignal);
        Assert.AreEqual("bsig1", boundarySignal.ActivityId);
        Assert.AreEqual("task1", boundarySignal.AttachedToActivityId);
        Assert.AreEqual("Signal_1", boundarySignal.SignalDefinitionId);
    }
}
