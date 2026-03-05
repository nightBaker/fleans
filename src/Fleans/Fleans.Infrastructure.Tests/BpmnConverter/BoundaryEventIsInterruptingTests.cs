using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class BoundaryEventIsInterruptingTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ParseBoundaryEvent_NonInterruptingTimer_SetsIsInterruptingFalse()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bt1"" attachedToRef=""task1"" cancelActivity=""false"">
      <timerEventDefinition>
        <timeDuration>PT10S</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bt1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        var boundary = workflow.Activities.OfType<BoundaryTimerEvent>().Single();
        Assert.IsFalse(boundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ParseBoundaryEvent_NoAttribute_DefaultsToInterrupting()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bt1"" attachedToRef=""task1"">
      <timerEventDefinition>
        <timeDuration>PT10S</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bt1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        var boundary = workflow.Activities.OfType<BoundaryTimerEvent>().Single();
        Assert.IsTrue(boundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ParseBoundaryEvent_CancelActivityTrue_SetsIsInterruptingTrue()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bt1"" attachedToRef=""task1"" cancelActivity=""true"">
      <timerEventDefinition>
        <timeDuration>PT10S</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bt1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        var boundary = workflow.Activities.OfType<BoundaryTimerEvent>().Single();
        Assert.IsTrue(boundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ParseBoundaryEvent_NonInterruptingMessage_SetsIsInterruptingFalse()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0"">
  <message id=""msg1"" name=""TestMessage"">
    <extensionElements>
      <zeebe:subscription correlationKey=""= orderId"" />
    </extensionElements>
  </message>
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bm1"" attachedToRef=""task1"" cancelActivity=""false"">
      <messageEventDefinition messageRef=""msg1"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bm1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        var boundary = workflow.Activities.OfType<MessageBoundaryEvent>().Single();
        Assert.IsFalse(boundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ParseBoundaryEvent_NonInterruptingSignal_SetsIsInterruptingFalse()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <signal id=""sig1"" name=""TestSignal"" />
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bs1"" attachedToRef=""task1"" cancelActivity=""false"">
      <signalEventDefinition signalRef=""sig1"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bs1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        var boundary = workflow.Activities.OfType<SignalBoundaryEvent>().Single();
        Assert.IsFalse(boundary.IsInterrupting);
    }

    [TestMethod]
    public async Task ParseBoundaryEvent_ErrorBoundary_AlwaysInterrupting()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""be1"" attachedToRef=""task1"" cancelActivity=""false"">
      <errorEventDefinition errorRef=""err500"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""be1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        var boundary = workflow.Activities.OfType<BoundaryErrorEvent>().Single();
        // Error boundaries are ALWAYS interrupting per BPMN spec
        Assert.IsTrue(boundary.IsInterrupting);
    }
}
