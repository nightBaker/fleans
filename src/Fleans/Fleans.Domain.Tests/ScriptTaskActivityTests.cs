using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class ScriptTaskActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldPublishExecuteScriptEvent_WithCorrectScriptAndFormat()
    {
        // Arrange
        var script = new ScriptTask("script1", "_context.x = 10", "csharp");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [script, end],
            [new SequenceFlow("seq1", script, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("script1");

        // Act
        await script.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert
        var scriptEvent = publishedEvents.OfType<ExecuteScriptEvent>().Single();
        Assert.AreEqual("_context.x = 10", scriptEvent.Script);
        Assert.AreEqual("csharp", scriptEvent.ScriptFormat);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldIncludeCorrectIds_InPublishedEvent()
    {
        // Arrange
        var workflowInstanceId = Guid.NewGuid();
        var activityInstanceId = Guid.NewGuid();

        var script = new ScriptTask("script1", "_context.x = 10");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [script],
            [],
            workflowId: "wf1",
            processDefinitionId: "pd1");
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) =
            ActivityTestHelper.CreateActivityContext("script1", activityInstanceId);

        // Act
        await script.ExecuteAsync(workflowContext, activityContext, workflowInstanceId);

        // Assert
        var scriptEvent = publishedEvents.OfType<ExecuteScriptEvent>().Single();
        Assert.AreEqual(workflowInstanceId, scriptEvent.WorkflowInstanceId);
        Assert.AreEqual("wf1", scriptEvent.WorkflowId);
        Assert.AreEqual("pd1", scriptEvent.ProcessDefinitionId);
        Assert.AreEqual(activityInstanceId, scriptEvent.ActivityInstanceId);
        Assert.AreEqual("script1", scriptEvent.ActivityId);
    }
}
