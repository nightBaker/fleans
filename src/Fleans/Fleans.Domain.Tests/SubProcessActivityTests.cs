using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SubProcessActivityTests
{
    [TestMethod]
    public void SubProcess_ShouldImplementIWorkflowDefinition()
    {
        // Arrange & Act
        var subProcess = new SubProcess("sp1")
        {
            Activities = [new StartEvent("sp-start"), new EndEvent("sp-end")],
            SequenceFlows = []
        };

        // Assert
        IWorkflowDefinition def = subProcess;
        Assert.AreEqual("sp1", def.WorkflowId);
        Assert.IsNull(def.ProcessDefinitionId);
        Assert.AreEqual(2, def.Activities.Count);
        Assert.IsEmpty(def.SequenceFlows);
        Assert.IsEmpty(def.Messages);
        Assert.IsEmpty(def.Signals);
    }

    [TestMethod]
    public void SubProcess_GetActivity_ShouldReturnCorrectChild()
    {
        // Arrange
        var inner = new TaskActivity("sp-task");
        var subProcess = new SubProcess("sp1") { Activities = [inner] };

        // Act
        var result = ((IWorkflowDefinition)subProcess).GetActivity("sp-task");

        // Assert
        Assert.AreEqual(inner, result);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_PublishEvent_AndOpenScope()
    {
        // Arrange
        var subProcess = new SubProcess("sp1")
        {
            Activities = [new StartEvent("sp-start"), new EndEvent("sp-end")],
            SequenceFlows = [new SequenceFlow("f1", new StartEvent("sp-start"), new EndEvent("sp-end"))]
        };
        var end = new EndEvent("end");
        var parentDefinition = ActivityTestHelper.CreateWorkflowDefinition(
            [subProcess, end],
            [new SequenceFlow("f0", subProcess, end)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(parentDefinition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("sp1");

        // Act
        await subProcess.ExecuteAsync(workflowContext, activityContext, parentDefinition);

        // Assert
        await activityContext.Received(1).Execute();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("sp1", executedEvent.activityId);
        Assert.AreEqual("SubProcess", executedEvent.TypeName);
        await workflowContext.Received(1).OpenSubProcessScope(
            Arg.Any<Guid>(), subProcess, Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldFollowOutgoingFlowFromParentDefinition()
    {
        // Arrange
        var subProcess = new SubProcess("sp1") { Activities = [], SequenceFlows = [] };
        var end = new EndEvent("end");
        var parentDefinition = ActivityTestHelper.CreateWorkflowDefinition(
            [subProcess, end],
            [new SequenceFlow("f1", subProcess, end)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(parentDefinition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sp1");

        // Act
        var nextActivities = await subProcess.GetNextActivities(workflowContext, activityContext, parentDefinition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var subProcess = new SubProcess("sp1") { Activities = [], SequenceFlows = [] };
        var parentDefinition = ActivityTestHelper.CreateWorkflowDefinition([subProcess], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(parentDefinition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sp1");

        // Act
        var nextActivities = await subProcess.GetNextActivities(workflowContext, activityContext, parentDefinition);

        // Assert
        Assert.IsEmpty(nextActivities);
    }
}
