using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class TaskActivityTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseWorkflow_WithTask()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithTask("workflow2", "task1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.AreEqual(3, workflow.Activities.Count);
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity && a.ActivityId == "task1"));
        Assert.AreEqual(2, workflow.SequenceFlows.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseUserTask_AsTaskActivity()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithUserTask("workflow3", "userTask1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity && a.ActivityId == "userTask1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseServiceTask_AsTaskActivity()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithServiceTask("workflow4", "serviceTask1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity && a.ActivityId == "serviceTask1"));
    }
}
