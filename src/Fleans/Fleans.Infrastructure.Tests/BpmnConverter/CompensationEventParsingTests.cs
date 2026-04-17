using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class CompensationEventParsingTests : BpmnConverterTestBase
{
    private static string CompensationBpmnXml => @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-process"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <intermediateThrowEvent id=""throw_comp"">
      <compensateEventDefinition />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""throw_comp"" />
    <sequenceFlow id=""f3"" sourceRef=""throw_comp"" targetRef=""end"" />
  </process>
</definitions>";

    [TestMethod]
    public async Task ParseCompensationBoundaryEvent_ShouldCreateBoundaryWithHandlerReference()
    {
        // Act
        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(CompensationBpmnXml)));

        // Assert: compensation boundary event is parsed
        var cbA = workflow.Activities.OfType<CompensationBoundaryEvent>().FirstOrDefault();
        Assert.IsNotNull(cbA, "CompensationBoundaryEvent should be parsed");
        Assert.AreEqual("cb_a", cbA.ActivityId);
        Assert.AreEqual("task_a", cbA.AttachedToActivityId);
        Assert.AreEqual("handler_a", cbA.HandlerActivityId,
            "Handler activity ID should be resolved from the <association> element");
    }

    [TestMethod]
    public async Task ParseCompensationIntermediateThrowEvent_BroadcastShouldHaveNullTarget()
    {
        // Act
        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(CompensationBpmnXml)));

        // Assert: broadcast compensation throw (no activityRef) has null TargetActivityRef
        var throwEvent = workflow.Activities.OfType<CompensationIntermediateThrowEvent>().FirstOrDefault();
        Assert.IsNotNull(throwEvent, "CompensationIntermediateThrowEvent should be parsed");
        Assert.AreEqual("throw_comp", throwEvent.ActivityId);
        Assert.IsNull(throwEvent.TargetActivityRef, "Broadcast throw should have null TargetActivityRef");
    }

    [TestMethod]
    public async Task ParseCompensationIntermediateThrowEvent_TargetedShouldHaveActivityRef()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-targeted"">
    <startEvent id=""start"" />
    <intermediateThrowEvent id=""throw_targeted"">
      <compensateEventDefinition activityRef=""task_a"" />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""throw_targeted"" />
    <sequenceFlow id=""f2"" sourceRef=""throw_targeted"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var throwEvent = workflow.Activities.OfType<CompensationIntermediateThrowEvent>().FirstOrDefault();
        Assert.IsNotNull(throwEvent);
        Assert.AreEqual("task_a", throwEvent.TargetActivityRef,
            "Targeted throw should have activityRef from compensateEventDefinition");
    }

    [TestMethod]
    public async Task ParseCompensationEndEvent_ShouldCreateCompensationEndEvent()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-end-process"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <endEvent id=""comp_end"">
      <compensateEventDefinition />
    </endEvent>
    <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""comp_end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        // A CompensationEndEvent should be created instead of a plain EndEvent
        var compEnd = workflow.Activities.OfType<CompensationEndEvent>().FirstOrDefault();
        Assert.IsNotNull(compEnd, "CompensationEndEvent should be parsed");
        Assert.AreEqual("comp_end", compEnd.ActivityId);

        // There should be no plain EndEvent for comp_end
        var plainEnd = workflow.Activities.OfType<EndEvent>()
            .FirstOrDefault(e => e.ActivityId == "comp_end");
        Assert.IsNull(plainEnd, "comp_end should not be parsed as a plain EndEvent");
    }

    [TestMethod]
    public async Task ParseCompensationBoundaryEvent_MissingAssociation_ShouldThrow()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-no-assoc"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""end"" />
  </process>
</definitions>";

        // No <association> linking cb_a to a handler — should throw
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [TestMethod]
    public async Task ParseCompensationBoundaryEvent_MultipleBoundaries_ShouldResolveEachAssociation()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-multi"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <scriptTask id=""task_b"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <boundaryEvent id=""cb_b"" attachedToRef=""task_b"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <scriptTask id=""handler_b"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
    <association id=""assoc2"" sourceRef=""cb_b"" targetRef=""handler_b"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""task_b"" />
    <sequenceFlow id=""f3"" sourceRef=""task_b"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var boundaries = workflow.Activities.OfType<CompensationBoundaryEvent>().ToList();
        Assert.AreEqual(2, boundaries.Count, "Both compensation boundary events should be parsed");

        var cbA = boundaries.First(b => b.ActivityId == "cb_a");
        var cbB = boundaries.First(b => b.ActivityId == "cb_b");

        Assert.AreEqual("handler_a", cbA.HandlerActivityId);
        Assert.AreEqual("handler_b", cbB.HandlerActivityId);
    }

    [TestMethod]
    public async Task ParseCompensationBoundaryEvent_CancelActivityTrue_ShouldThrow()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-cancel-true"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""true"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""end"" />
  </process>
</definitions>";

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [TestMethod]
    public async Task ParseCompensationBoundaryEvent_MultipleBoundariesOnSameActivity_ShouldThrow()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-dup-boundary"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a1"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <boundaryEvent id=""cb_a2"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a1"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <scriptTask id=""handler_a2"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_a1"" targetRef=""handler_a1"" associationDirection=""One"" />
    <association id=""assoc2"" sourceRef=""cb_a2"" targetRef=""handler_a2"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""end"" />
  </process>
</definitions>";

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [TestMethod]
    public async Task ParseCompensationThrow_ActivityRefPointsToNonCompensableActivity_ShouldThrow()
    {
        // task_b has a compensation boundary, but the throw targets task_a which does NOT
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-bad-ref"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <scriptTask id=""task_b"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_b"" attachedToRef=""task_b"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_b"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <intermediateThrowEvent id=""throw_comp"">
      <compensateEventDefinition activityRef=""task_a"" />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_b"" targetRef=""handler_b"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""task_b"" />
    <sequenceFlow id=""f3"" sourceRef=""task_b"" targetRef=""throw_comp"" />
    <sequenceFlow id=""f4"" sourceRef=""throw_comp"" targetRef=""end"" />
  </process>
</definitions>";

        // task_a has no CompensationBoundaryEvent, so activityRef="task_a" is invalid
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [TestMethod]
    public async Task ParseCompensationHandler_WithIncomingSequenceFlow_ShouldThrow()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-handler-incoming"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""task_a"" targetRef=""handler_a"" />
  </process>
</definitions>";

        // handler_a has an incoming sequence flow (f3), which is not allowed
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [TestMethod]
    public async Task ParseCompensationHandler_WithOwnCompensationBoundary_ShouldThrow()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-handler-nested"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_handler"" attachedToRef=""handler_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_handler"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <endEvent id=""end"" />
    <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
    <association id=""assoc2"" sourceRef=""cb_handler"" targetRef=""handler_handler"" associationDirection=""One"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""end"" />
  </process>
</definitions>";

        // handler_a itself has a CompensationBoundaryEvent — not allowed
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [TestMethod]
    public async Task ParseCompensation_AssociationAtDefinitionsLevel_ShouldParse()
    {
        // Association placed directly under <definitions>, not under <process>
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""comp-defn-assoc"">
    <startEvent id=""start"" />
    <scriptTask id=""task_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <boundaryEvent id=""cb_a"" attachedToRef=""task_a"" cancelActivity=""false"">
      <compensateEventDefinition />
    </boundaryEvent>
    <scriptTask id=""handler_a"" scriptFormat=""csharp""><script>pass</script></scriptTask>
    <intermediateThrowEvent id=""throw_comp"">
      <compensateEventDefinition />
    </intermediateThrowEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task_a"" />
    <sequenceFlow id=""f2"" sourceRef=""task_a"" targetRef=""throw_comp"" />
    <sequenceFlow id=""f3"" sourceRef=""throw_comp"" targetRef=""end"" />
  </process>
  <association id=""assoc1"" sourceRef=""cb_a"" targetRef=""handler_a"" associationDirection=""One"" />
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var cbA = workflow.Activities.OfType<CompensationBoundaryEvent>().FirstOrDefault();
        Assert.IsNotNull(cbA, "CompensationBoundaryEvent should be parsed");
        Assert.AreEqual("handler_a", cbA.HandlerActivityId,
            "Handler should be resolved from association at definitions level");
    }

    [TestMethod]
    public async Task ParseWorkflowWithCompensation_SequenceFlowsShouldBeWiredCorrectly()
    {
        var workflow = await _converter.ConvertFromXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(CompensationBpmnXml)));

        // Verify sequence flow: throw_comp → end
        var throwEvent = workflow.Activities.OfType<CompensationIntermediateThrowEvent>().First();
        var outFlow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == throwEvent);
        Assert.IsNotNull(outFlow, "CompensationIntermediateThrowEvent should have an outgoing sequence flow");
        Assert.AreEqual("end", outFlow.Target.ActivityId);
    }
}
