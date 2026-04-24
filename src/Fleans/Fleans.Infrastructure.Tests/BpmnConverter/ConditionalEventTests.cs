using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class ConditionalEventTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseConditionalIntermediateCatchEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitCondition"">
      <conditionalEventDefinition>
        <condition>amount > 1000</condition>
      </conditionalEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitCondition"" />
    <sequenceFlow id=""f2"" sourceRef=""waitCondition"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var condCatch = workflow.Activities.OfType<ConditionalIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(condCatch);
        Assert.AreEqual("waitCondition", condCatch.ActivityId);
        Assert.AreEqual("amount > 1000", condCatch.ConditionExpression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseConditionalBoundaryEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"" />
    <boundaryEvent id=""bcond1"" attachedToRef=""task1"">
      <conditionalEventDefinition>
        <condition>status == ""approved""</condition>
      </conditionalEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""condEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bcond1"" targetRef=""condEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var boundaryCond = workflow.Activities.OfType<ConditionalBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(boundaryCond);
        Assert.AreEqual("bcond1", boundaryCond.ActivityId);
        Assert.AreEqual("task1", boundaryCond.AttachedToActivityId);
        Assert.AreEqual("status == \"approved\"", boundaryCond.ConditionExpression);
        Assert.IsTrue(boundaryCond.IsInterrupting);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseNonInterruptingConditionalBoundaryEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"" />
    <boundaryEvent id=""bcond1"" attachedToRef=""task1"" cancelActivity=""false"">
      <conditionalEventDefinition>
        <condition>counter > 5</condition>
      </conditionalEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""condEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bcond1"" targetRef=""condEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var boundaryCond = workflow.Activities.OfType<ConditionalBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(boundaryCond);
        Assert.IsFalse(boundaryCond.IsInterrupting);
        Assert.AreEqual("counter > 5", boundaryCond.ConditionExpression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseConditionalStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""condStart"">
      <conditionalEventDefinition>
        <condition>temperature > 100</condition>
      </conditionalEventDefinition>
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""condStart"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var condStart = workflow.Activities.OfType<ConditionalStartEvent>().SingleOrDefault();
        Assert.IsNotNull(condStart);
        Assert.AreEqual("condStart", condStart.ActivityId);
        Assert.AreEqual("temperature > 100", condStart.ConditionExpression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrowForEmptyCondition()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""condStart"">
      <conditionalEventDefinition>
        <condition></condition>
      </conditionalEventDefinition>
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""condStart"" targetRef=""end"" />
  </process>
</definitions>";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml))));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrowForMissingConditionElement()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""condStart"">
      <conditionalEventDefinition />
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""condStart"" targetRef=""end"" />
  </process>
</definitions>";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml))));
    }
}
