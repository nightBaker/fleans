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
    public async Task ConvertFromXmlAsync_ShouldParseUserTask_AsUserTask()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithUserTask("workflow3", "userTask1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is UserTask && a.ActivityId == "userTask1"));
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

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseUserTask_WithAllAttributes()
    {
        var bpmnXml = CreateBpmnWithUserTaskAttributes("wf", "ut1",
            assignee: "alice", candidateGroups: "managers,admins", candidateUsers: "bob,carol",
            expectedOutputs: ["result"]);

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var userTask = workflow.Activities.OfType<UserTask>().Single(a => a.ActivityId == "ut1");
        Assert.AreEqual("alice", userTask.Assignee);
        CollectionAssert.AreEquivalent(new[] { "managers", "admins" }, userTask.CandidateGroups.ToList());
        CollectionAssert.AreEquivalent(new[] { "bob", "carol" }, userTask.CandidateUsers.ToList());
        CollectionAssert.AreEquivalent(new[] { "result" }, userTask.ExpectedOutputVariables!.ToList());
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseUserTask_WithNoAttributes_DefaultsToEmpty()
    {
        var bpmnXml = CreateBpmnWithUserTask("wf", "ut1");

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var userTask = workflow.Activities.OfType<UserTask>().Single(a => a.ActivityId == "ut1");
        Assert.IsNull(userTask.Assignee);
        Assert.AreEqual(0, userTask.CandidateGroups.Count);
        Assert.AreEqual(0, userTask.CandidateUsers.Count);
        Assert.IsNull(userTask.ExpectedOutputVariables);
    }
}
