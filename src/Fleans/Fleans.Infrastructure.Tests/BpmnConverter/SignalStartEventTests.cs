using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class SignalStartEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSignalStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:bpmndi=""http://www.omg.org/spec/BPMN/20100524/DI"">
  <signal id=""sig1"" name=""orderSignal"" />
  <process id=""signal-start-workflow"">
    <startEvent id=""sigStart1"">
      <signalEventDefinition signalRef=""sig1"" />
    </startEvent>
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script></script>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""sigStart1"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
  </process>
  <bpmndi:BPMNDiagram id=""BPMNDiagram_1"">
    <bpmndi:BPMNPlane id=""BPMNPlane_1"" bpmnElement=""signal-start-workflow"" />
  </bpmndi:BPMNDiagram>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var signalStart = workflow.Activities.OfType<SignalStartEvent>().FirstOrDefault();
        Assert.IsNotNull(signalStart);
        Assert.AreEqual("sigStart1", signalStart.ActivityId);
        Assert.AreEqual("sig1", signalStart.SignalDefinitionId);

        // Should not produce a plain StartEvent for this element
        Assert.IsFalse(workflow.Activities.Any(a => a is StartEvent && a is not SignalStartEvent && a.ActivityId == "sigStart1"));

        // Signal definition should be parsed
        Assert.HasCount(1, workflow.Signals);
        Assert.AreEqual("sig1", workflow.Signals[0].Id);
        Assert.AreEqual("orderSignal", workflow.Signals[0].Name);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrow_WhenSignalRefMissing()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:bpmndi=""http://www.omg.org/spec/BPMN/20100524/DI"">
  <process id=""bad-workflow"">
    <startEvent id=""sigStart1"">
      <signalEventDefinition />
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""sigStart1"" targetRef=""end"" />
  </process>
  <bpmndi:BPMNDiagram id=""BPMNDiagram_1"">
    <bpmndi:BPMNPlane id=""BPMNPlane_1"" bpmnElement=""bad-workflow"" />
  </bpmndi:BPMNDiagram>
</definitions>";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml))));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSequenceFlow_FromSignalStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:bpmndi=""http://www.omg.org/spec/BPMN/20100524/DI"">
  <signal id=""sig1"" name=""orderSignal"" />
  <process id=""signal-start-workflow"">
    <startEvent id=""sigStart1"">
      <signalEventDefinition signalRef=""sig1"" />
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""sigStart1"" targetRef=""end"" />
  </process>
  <bpmndi:BPMNDiagram id=""BPMNDiagram_1"">
    <bpmndi:BPMNPlane id=""BPMNPlane_1"" bpmnElement=""signal-start-workflow"" />
  </bpmndi:BPMNDiagram>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source.ActivityId == "sigStart1");
        Assert.IsNotNull(flow);
        Assert.AreEqual("end", flow.Target.ActivityId);
    }
}
