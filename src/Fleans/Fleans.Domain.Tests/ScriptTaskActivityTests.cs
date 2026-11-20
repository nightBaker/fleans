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
        await script.ExecuteAsync(workflowContext, activityContext, definition);

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
        workflowContext.GetWorkflowInstanceId().Returns(ValueTask.FromResult(workflowInstanceId));
        var (activityContext, publishedEvents) =
            ActivityTestHelper.CreateActivityContext("script1", activityInstanceId);

        // Act
        await script.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var scriptEvent = publishedEvents.OfType<ExecuteScriptEvent>().Single();
        Assert.AreEqual(workflowInstanceId, scriptEvent.WorkflowInstanceId);
        Assert.AreEqual("wf1", scriptEvent.WorkflowId);
        Assert.AreEqual("pd1", scriptEvent.ProcessDefinitionId);
        Assert.AreEqual(activityInstanceId, scriptEvent.ActivityInstanceId);
        Assert.AreEqual("script1", scriptEvent.ActivityId);
        Assert.AreEqual("csharp", scriptEvent.ScriptFormat, "format defaults to csharp");
    }

    [TestMethod]
    [DataRow("javascript", "fleans.script.javascript")]
    [DataRow("JS", "fleans.script.javascript")]
    [DataRow("py", "fleans.script.python")]
    [DataRow("Groovy", "fleans.script.groovy")]
    public async Task ExecuteAsync_NonCSharpFormat_PublishesACustomTaskForItsWorker(string format, string expectedTaskType)
    {
        // Arrange
        var activityInstanceId = Guid.NewGuid();
        var script = new ScriptTask("script1", "_context.x = 1;", format);
        var definition = ActivityTestHelper.CreateWorkflowDefinition([script], [], workflowId: "wf1");
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("script1", activityInstanceId);

        // Act
        await script.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        Assert.IsEmpty(publishedEvents.OfType<ExecuteScriptEvent>().ToList());
        var task = publishedEvents.OfType<ExecuteCustomTaskEvent>().Single();
        Assert.AreEqual(expectedTaskType, task.TaskType);
        Assert.AreEqual("script1", task.ActivityId);
        Assert.AreEqual(activityInstanceId, task.ActivityInstanceId);
        Assert.AreEqual("_context.x = 1;", ((IDictionary<string, object?>)task.Inputs)["script"]);
        Assert.IsNull(task.TaskVersion);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow("csharp", true)]
    [DataRow("C#", true)]
    [DataRow("javascript", false)]
    public void ScriptFormats_IsBuiltIn_OnlyForCSharp(string? format, bool expected) =>
        Assert.AreEqual(expected, ScriptFormats.IsBuiltIn(format));
}
