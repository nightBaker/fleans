using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MessageStartEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseMessageStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""orderReceived"" />
  <process id=""message-start-workflow"">
    <startEvent id=""msgStart1"">
      <messageEventDefinition messageRef=""msg1"" />
    </startEvent>
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script></script>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""msgStart1"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var messageStart = workflow.Activities.OfType<MessageStartEvent>().FirstOrDefault();
        Assert.IsNotNull(messageStart);
        Assert.AreEqual("msgStart1", messageStart.ActivityId);
        Assert.AreEqual("msg1", messageStart.MessageDefinitionId);

        // Should not produce a plain StartEvent for this element
        Assert.IsFalse(workflow.Activities.Any(a => a is StartEvent && a is not MessageStartEvent && a.ActivityId == "msgStart1"));

        // Message definition should be parsed
        Assert.HasCount(1, workflow.Messages);
        Assert.AreEqual("msg1", workflow.Messages[0].Id);
        Assert.AreEqual("orderReceived", workflow.Messages[0].Name);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrow_WhenMessageRefMissing()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""bad-workflow"">
    <startEvent id=""msgStart1"">
      <messageEventDefinition />
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""msgStart1"" targetRef=""end"" />
  </process>
</definitions>";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml))));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSequenceFlow_FromMessageStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""orderReceived"" />
  <process id=""message-start-workflow"">
    <startEvent id=""msgStart1"">
      <messageEventDefinition messageRef=""msg1"" />
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""msgStart1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source.ActivityId == "msgStart1");
        Assert.IsNotNull(flow);
        Assert.AreEqual("end", flow.Target.ActivityId);
    }
}
