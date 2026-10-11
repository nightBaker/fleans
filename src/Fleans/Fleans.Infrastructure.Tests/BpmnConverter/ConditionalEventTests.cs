using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Infrastructure.Conditions;
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
        Assert.AreEqual("_context.amount > 1000", condCatch.ConditionExpression);
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
        Assert.AreEqual("_context.status == \"approved\"", boundaryCond.ConditionExpression);
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
        Assert.AreEqual("_context.counter > 5", boundaryCond.ConditionExpression);
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
        Assert.AreEqual("_context.temperature > 100", condStart.ConditionExpression);
    }

    // Regression for #760: conditional-event conditions are written with bare variable
    // names (same as sequence-flow conditions) and must be rewritten to `_context.<name>`
    // so the DynamicExpresso evaluator can resolve them against workflow variables.
    [TestMethod]
    [DataRow("${temperature > 100}", "_context.temperature > 100")]
    [DataRow("_context.temperature > 100", "_context.temperature > 100")]
    [DataRow("temperature > 100 && unit == \"C\"", "_context.temperature > 100 && _context.unit == \"C\"")]
    public async Task ConvertFromXmlAsync_ConditionalStartEvent_NormalizesConditionSyntax(string condition, string expected)
    {
        var bpmnXml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""condStart"">
      <conditionalEventDefinition>
        <condition>{System.Security.SecurityElement.Escape(condition)}</condition>
      </conditionalEventDefinition>
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""condStart"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var condStart = workflow.Activities.OfType<ConditionalStartEvent>().Single();
        Assert.AreEqual(expected, condStart.ConditionExpression);
    }

    [TestMethod]
    [DataRow(150, true)]
    [DataRow(50, false)]
    public async Task ConditionalStartEvent_ParsedCondition_EvaluatesAgainstVariables(int temperature, bool expected)
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""condStart"">
      <conditionalEventDefinition>
        <condition>temperature &gt; 100</condition>
      </conditionalEventDefinition>
    </startEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""condStart"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var condStart = workflow.Activities.OfType<ConditionalStartEvent>().Single();

        dynamic variables = new ExpandoObject();
        variables.temperature = (long)temperature;
        var result = await new DynamicExpressoConditionExpressionEvaluator()
            .Evaluate(condStart.ConditionExpression, variables);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    [DataRow(600L, true)]
    [DataRow(0L, false)]
    public async Task ConditionalIntermediateCatchEvent_ParsedCondition_EvaluatesAgainstVariables(long amount, bool expected)
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""process1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitCondition"">
      <conditionalEventDefinition>
        <condition>amount > 500</condition>
      </conditionalEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitCondition"" />
    <sequenceFlow id=""f2"" sourceRef=""waitCondition"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var condCatch = workflow.Activities.OfType<ConditionalIntermediateCatchEvent>().Single();

        dynamic variables = new ExpandoObject();
        variables.amount = amount;
        var result = await new DynamicExpressoConditionExpressionEvaluator()
            .Evaluate(condCatch.ConditionExpression, variables);

        Assert.AreEqual(expected, result);
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
