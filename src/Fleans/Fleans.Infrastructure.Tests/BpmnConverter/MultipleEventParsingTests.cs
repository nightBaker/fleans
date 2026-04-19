using System.Text;
using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MultipleEventParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task IntermediateCatchEvent_MessageAndSignal_ParsesAsMultipleIntermediateCatchEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <signal id=""sig1"" name=""orderApproved"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""ev1"">
      <messageEventDefinition messageRef=""msg1"" />
      <signalEventDefinition signalRef=""sig1"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ev1"" />
    <sequenceFlow id=""f2"" sourceRef=""ev1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var multiple = workflow.Activities.OfType<MultipleIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(multiple);
        Assert.AreEqual("ev1", multiple.ActivityId);
        Assert.AreEqual(2, multiple.Definitions.Count);

        var msgDef = multiple.Definitions.OfType<MessageEventDef>().SingleOrDefault();
        Assert.IsNotNull(msgDef);
        Assert.AreEqual("msg1", msgDef.MessageDefinitionId);

        var sigDef = multiple.Definitions.OfType<SignalEventDef>().SingleOrDefault();
        Assert.IsNotNull(sigDef);
        Assert.AreEqual("sig1", sigDef.SignalDefinitionId);
    }

    [TestMethod]
    public async Task IntermediateCatchEvent_MessageAndTimer_ParsesAsMultipleIntermediateCatchEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""ev1"">
      <messageEventDefinition messageRef=""msg1"" />
      <timerEventDefinition>
        <timeDuration>PT1H</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ev1"" />
    <sequenceFlow id=""f2"" sourceRef=""ev1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var multiple = workflow.Activities.OfType<MultipleIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(multiple);
        Assert.AreEqual("ev1", multiple.ActivityId);
        Assert.AreEqual(2, multiple.Definitions.Count);

        var msgDef = multiple.Definitions.OfType<MessageEventDef>().SingleOrDefault();
        Assert.IsNotNull(msgDef);
        Assert.AreEqual("msg1", msgDef.MessageDefinitionId);

        var timerDef = multiple.Definitions.OfType<TimerEventDef>().SingleOrDefault();
        Assert.IsNotNull(timerDef);
        Assert.AreEqual(TimerType.Duration, timerDef.TimerDefinition.Type);
        Assert.AreEqual("PT1H", timerDef.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task IntermediateThrowEvent_TwoSignals_ParsesAsMultipleIntermediateThrowEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <signal id=""sig1"" name=""signalA"" />
  <signal id=""sig2"" name=""signalB"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateThrowEvent id=""ev1"">
      <signalEventDefinition signalRef=""sig1"" />
      <signalEventDefinition signalRef=""sig2"" />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ev1"" />
    <sequenceFlow id=""f2"" sourceRef=""ev1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var multiple = workflow.Activities.OfType<MultipleIntermediateThrowEvent>().SingleOrDefault();
        Assert.IsNotNull(multiple);
        Assert.AreEqual("ev1", multiple.ActivityId);
        Assert.AreEqual(2, multiple.Definitions.Count);

        var sigDefs = multiple.Definitions.OfType<SignalEventDef>().ToList();
        Assert.AreEqual(2, sigDefs.Count);
        Assert.IsTrue(sigDefs.Any(d => d.SignalDefinitionId == "sig1"));
        Assert.IsTrue(sigDefs.Any(d => d.SignalDefinitionId == "sig2"));
    }

    [TestMethod]
    public async Task BoundaryEvent_MessageAndTimer_ParsesAsMultipleBoundaryEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""cancelOrder"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bnd1"" attachedToRef=""task1"" cancelActivity=""false"">
      <messageEventDefinition messageRef=""msg1"" />
      <timerEventDefinition>
        <timeDuration>PT30S</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""bndEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bnd1"" targetRef=""bndEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var multiple = workflow.Activities.OfType<MultipleBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(multiple);
        Assert.AreEqual("bnd1", multiple.ActivityId);
        Assert.AreEqual("task1", multiple.AttachedToActivityId);
        Assert.IsFalse(multiple.IsInterrupting);
        Assert.AreEqual(2, multiple.Definitions.Count);

        var msgDef = multiple.Definitions.OfType<MessageEventDef>().SingleOrDefault();
        Assert.IsNotNull(msgDef);
        Assert.AreEqual("msg1", msgDef.MessageDefinitionId);

        var timerDef = multiple.Definitions.OfType<TimerEventDef>().SingleOrDefault();
        Assert.IsNotNull(timerDef);
        Assert.AreEqual(TimerType.Duration, timerDef.TimerDefinition.Type);
        Assert.AreEqual("PT30S", timerDef.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task StartEvent_MessageAndSignal_ParsesAsMultipleStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""startMessage"" />
  <signal id=""sig1"" name=""startSignal"" />
  <process id=""process1"">
    <startEvent id=""start1"">
      <messageEventDefinition messageRef=""msg1"" />
      <signalEventDefinition signalRef=""sig1"" />
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var multiple = workflow.Activities.OfType<MultipleStartEvent>().SingleOrDefault();
        Assert.IsNotNull(multiple);
        Assert.AreEqual("start1", multiple.ActivityId);
        Assert.AreEqual(2, multiple.Definitions.Count);

        var msgDef = multiple.Definitions.OfType<MessageEventDef>().SingleOrDefault();
        Assert.IsNotNull(msgDef);
        Assert.AreEqual("msg1", msgDef.MessageDefinitionId);

        var sigDef = multiple.Definitions.OfType<SignalEventDef>().SingleOrDefault();
        Assert.IsNotNull(sigDef);
        Assert.AreEqual("sig1", sigDef.SignalDefinitionId);
    }

    [TestMethod]
    public async Task IntermediateCatchEvent_SingleDefinition_ParsesAsSingleType()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""ev1"">
      <messageEventDefinition messageRef=""msg1"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ev1"" />
    <sequenceFlow id=""f2"" sourceRef=""ev1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var msgCatch = workflow.Activities.OfType<MessageIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(msgCatch, "Single messageEventDefinition should parse as MessageIntermediateCatchEvent");
        Assert.AreEqual("ev1", msgCatch.ActivityId);

        var multiple = workflow.Activities.OfType<MultipleIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNull(multiple, "Single definition must not produce MultipleIntermediateCatchEvent");
    }

    [TestMethod]
    public async Task IntermediateCatchEvent_ThreeDefinitions_ParsesAsMultipleIntermediateCatchEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <signal id=""sig1"" name=""orderApproved"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""ev1"">
      <messageEventDefinition messageRef=""msg1"" />
      <signalEventDefinition signalRef=""sig1"" />
      <timerEventDefinition>
        <timeDuration>PT5M</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ev1"" />
    <sequenceFlow id=""f2"" sourceRef=""ev1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var multiple = workflow.Activities.OfType<MultipleIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(multiple);
        Assert.AreEqual("ev1", multiple.ActivityId);
        Assert.AreEqual(3, multiple.Definitions.Count);

        var msgDef = multiple.Definitions.OfType<MessageEventDef>().SingleOrDefault();
        Assert.IsNotNull(msgDef);
        Assert.AreEqual("msg1", msgDef.MessageDefinitionId);

        var sigDef = multiple.Definitions.OfType<SignalEventDef>().SingleOrDefault();
        Assert.IsNotNull(sigDef);
        Assert.AreEqual("sig1", sigDef.SignalDefinitionId);

        var timerDef = multiple.Definitions.OfType<TimerEventDef>().SingleOrDefault();
        Assert.IsNotNull(timerDef);
        Assert.AreEqual(TimerType.Duration, timerDef.TimerDefinition.Type);
        Assert.AreEqual("PT5M", timerDef.TimerDefinition.Expression);
    }
}
