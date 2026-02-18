using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class TimerEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithDuration()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeDuration>PT5M</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
        Assert.IsNotNull(timerEvent);
        Assert.AreEqual("timer1", timerEvent.ActivityId);
        Assert.AreEqual(TimerType.Duration, timerEvent.TimerDefinition.Type);
        Assert.AreEqual("PT5M", timerEvent.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithDate()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeDate>2026-03-01T10:00:00Z</timeDate>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
        Assert.IsNotNull(timerEvent);
        Assert.AreEqual(TimerType.Date, timerEvent.TimerDefinition.Type);
        Assert.AreEqual("2026-03-01T10:00:00Z", timerEvent.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithCycle()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeCycle>R3/PT10M</timeCycle>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
        Assert.IsNotNull(timerEvent);
        Assert.AreEqual(TimerType.Cycle, timerEvent.TimerDefinition.Type);
        Assert.AreEqual("R3/PT10M", timerEvent.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseBoundaryTimerEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <endEvent id=""end"" />
    <endEvent id=""timeoutEnd"" />
    <boundaryEvent id=""bt1"" attachedToRef=""task1"">
      <timerEventDefinition>
        <timeDuration>PT30M</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""bt1"" targetRef=""timeoutEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var boundaryTimer = workflow.Activities.OfType<BoundaryTimerEvent>().FirstOrDefault();
        Assert.IsNotNull(boundaryTimer);
        Assert.AreEqual("bt1", boundaryTimer.ActivityId);
        Assert.AreEqual("task1", boundaryTimer.AttachedToActivityId);
        Assert.AreEqual(TimerType.Duration, boundaryTimer.TimerDefinition.Type);
        Assert.AreEqual("PT30M", boundaryTimer.TimerDefinition.Expression);

        Assert.IsFalse(workflow.Activities.OfType<BoundaryErrorEvent>().Any(b => b.ActivityId == "bt1"));

        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == boundaryTimer);
        Assert.IsNotNull(flow);
        Assert.AreEqual("timeoutEnd", flow.Target.ActivityId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""scheduled-workflow"">
    <startEvent id=""timerStart1"">
      <timerEventDefinition>
        <timeCycle>R/PT1H</timeCycle>
      </timerEventDefinition>
    </startEvent>
    <task id=""task1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""timerStart1"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerStart = workflow.Activities.OfType<TimerStartEvent>().FirstOrDefault();
        Assert.IsNotNull(timerStart);
        Assert.AreEqual("timerStart1", timerStart.ActivityId);
        Assert.AreEqual(TimerType.Cycle, timerStart.TimerDefinition.Type);
        Assert.AreEqual("R/PT1H", timerStart.TimerDefinition.Expression);
        Assert.IsFalse(workflow.Activities.Any(a => a is StartEvent && a is not TimerStartEvent && a.ActivityId == "timerStart1"));
    }
}
