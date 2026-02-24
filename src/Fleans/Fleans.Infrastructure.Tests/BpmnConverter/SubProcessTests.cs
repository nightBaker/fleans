using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class SubProcessTests : BpmnConverterTestBase
{
    private static string CreateSimpleSubProcessBpmn() => @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""main-process"">
    <startEvent id=""start"" />
    <subProcess id=""sp1"">
      <startEvent id=""sp-start"" />
      <task id=""sp-task"" />
      <endEvent id=""sp-end"" />
      <sequenceFlow id=""sp-f1"" sourceRef=""sp-start"" targetRef=""sp-task"" />
      <sequenceFlow id=""sp-f2"" sourceRef=""sp-task"" targetRef=""sp-end"" />
    </subProcess>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""sp1"" />
    <sequenceFlow id=""f2"" sourceRef=""sp1"" targetRef=""end"" />
  </process>
</definitions>";

    private static string CreateNestedSubProcessBpmn() => @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""main-process"">
    <startEvent id=""start"" />
    <subProcess id=""outer-sp"">
      <startEvent id=""outer-start"" />
      <subProcess id=""inner-sp"">
        <startEvent id=""inner-start"" />
        <task id=""inner-task"" />
        <endEvent id=""inner-end"" />
        <sequenceFlow id=""if1"" sourceRef=""inner-start"" targetRef=""inner-task"" />
        <sequenceFlow id=""if2"" sourceRef=""inner-task"" targetRef=""inner-end"" />
      </subProcess>
      <endEvent id=""outer-end"" />
      <sequenceFlow id=""of1"" sourceRef=""outer-start"" targetRef=""inner-sp"" />
      <sequenceFlow id=""of2"" sourceRef=""inner-sp"" targetRef=""outer-end"" />
    </subProcess>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""outer-sp"" />
    <sequenceFlow id=""f2"" sourceRef=""outer-sp"" targetRef=""end"" />
  </process>
</definitions>";

    private static string CreateSubProcessWithBoundaryTimerBpmn() => @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""main-process"">
    <startEvent id=""start"" />
    <subProcess id=""sp1"">
      <startEvent id=""sp-start"" />
      <task id=""sp-task"" />
      <endEvent id=""sp-end"" />
      <sequenceFlow id=""sp-f1"" sourceRef=""sp-start"" targetRef=""sp-task"" />
      <sequenceFlow id=""sp-f2"" sourceRef=""sp-task"" targetRef=""sp-end"" />
    </subProcess>
    <boundaryEvent id=""bt1"" attachedToRef=""sp1"">
      <timerEventDefinition><timeDuration>PT30M</timeDuration></timerEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""timeout-end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""sp1"" />
    <sequenceFlow id=""f2"" sourceRef=""sp1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bt1"" targetRef=""timeout-end"" />
  </process>
</definitions>";

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSubProcess_WithChildActivities()
    {
        // Act
        var workflow = await _converter.ConvertFromXmlAsync(
            new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(CreateSimpleSubProcessBpmn())));

        // Assert
        var sp = workflow.Activities.OfType<SubProcess>().SingleOrDefault();
        Assert.IsNotNull(sp, "SubProcess should be in root activities");
        Assert.AreEqual("sp1", sp.ActivityId);
        Assert.AreEqual(3, sp.Activities.Count); // start, task, end
        Assert.AreEqual(2, sp.SequenceFlows.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldNotLeakChildActivitiesIntoRoot()
    {
        // Act
        var workflow = await _converter.ConvertFromXmlAsync(
            new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(CreateSimpleSubProcessBpmn())));

        // Assert: root should only have start, sp1, end
        Assert.AreEqual(3, workflow.Activities.Count);
        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "sp-task"),
            "sp-task should not leak into root activities");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseNestedSubProcess()
    {
        // Act
        var workflow = await _converter.ConvertFromXmlAsync(
            new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(CreateNestedSubProcessBpmn())));

        // Assert
        var outerSp = workflow.Activities.OfType<SubProcess>().Single();
        Assert.AreEqual("outer-sp", outerSp.ActivityId);

        var innerSp = outerSp.Activities.OfType<SubProcess>().Single();
        Assert.AreEqual("inner-sp", innerSp.ActivityId);
        Assert.AreEqual(3, innerSp.Activities.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldAttachBoundaryEvent_ToSubProcess_InRootActivities()
    {
        // Boundary events on a sub-process belong to the root definition (not inside the sub-process)
        var workflow = await _converter.ConvertFromXmlAsync(
            new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(CreateSubProcessWithBoundaryTimerBpmn())));

        // Assert: boundary timer is in root activities
        var boundaryTimer = workflow.Activities.OfType<BoundaryTimerEvent>().SingleOrDefault();
        Assert.IsNotNull(boundaryTimer);
        Assert.AreEqual("bt1", boundaryTimer.ActivityId);
        Assert.AreEqual("sp1", boundaryTimer.AttachedToActivityId);

        // And it is NOT inside the sub-process
        var sp = workflow.Activities.OfType<SubProcess>().Single();
        Assert.IsFalse(sp.Activities.Any(a => a.ActivityId == "bt1"),
            "Boundary timer should not be inside the sub-process");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSubProcess_SequenceFlowsBetweenChildActivities()
    {
        var workflow = await _converter.ConvertFromXmlAsync(
            new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(CreateSimpleSubProcessBpmn())));

        var sp = workflow.Activities.OfType<SubProcess>().Single();
        var flow = sp.SequenceFlows.FirstOrDefault(sf => sf.Source.ActivityId == "sp-task");
        Assert.IsNotNull(flow);
        Assert.AreEqual("sp-end", flow.Target.ActivityId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_SubProcess_ShouldHaveOutgoingFlowInRootDefinition()
    {
        var workflow = await _converter.ConvertFromXmlAsync(
            new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(CreateSimpleSubProcessBpmn())));

        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source.ActivityId == "sp1");
        Assert.IsNotNull(flow);
        Assert.AreEqual("end", flow.Target.ActivityId);
    }
}
