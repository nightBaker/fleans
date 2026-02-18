using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class BasicWorkflowTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSimpleWorkflow_WithStartAndEnd()
    {
        // Arrange
        var bpmnXml = CreateSimpleBpmnXml("workflow1",
            startEventId: "start",
            endEventId: "end",
            sequenceFlowId: "flow1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsNotNull(workflow);
        Assert.AreEqual("workflow1", workflow.WorkflowId);
        Assert.AreEqual(2, workflow.Activities.Count);
        Assert.IsTrue(workflow.Activities.Any(a => a is StartEvent && a.ActivityId == "start"));
        Assert.IsTrue(workflow.Activities.Any(a => a is EndEvent && a.ActivityId == "end"));
        Assert.AreEqual(1, workflow.SequenceFlows.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrowException_WhenProcessElementMissing()
    {
        // Arrange
        var invalidXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
</definitions>";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalidXml)));
        });
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrowException_WhenProcessIdMissing()
    {
        // Arrange
        var invalidXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process>
    <startEvent id=""start"" />
  </process>
</definitions>";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalidXml)));
        });
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleComplexWorkflow_WithMultipleActivities()
    {
        // Arrange
        var bpmnXml = CreateComplexBpmnWorkflow("complexWorkflow");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.AreEqual("complexWorkflow", workflow.WorkflowId);
        Assert.IsTrue(workflow.Activities.Count >= 5);
        Assert.IsTrue(workflow.SequenceFlows.Count >= 4);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleWorkflow_WithAllActivityTypes()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithAllActivityTypes("workflow16");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is StartEvent));
        Assert.IsTrue(workflow.Activities.Any(a => a is EndEvent));
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity));
        Assert.IsTrue(workflow.Activities.Any(a => a is ExclusiveGateway));
        Assert.IsTrue(workflow.Activities.Any(a => a is ParallelGateway));
    }
}
