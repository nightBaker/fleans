using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundarableActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnRegisterTimerCommand_WhenBoundaryTimerAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task, boundaryTimer], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var activityInstanceId = Guid.NewGuid();
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1", activityInstanceId);

        // Act
        var commands = await task.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var timerCmd = commands.OfType<RegisterTimerCommand>().Single();
        Assert.AreEqual("bt1", timerCmd.TimerActivityId);
        Assert.AreEqual(TimeSpan.FromMinutes(10), timerCmd.DueTime);
        Assert.IsTrue(timerCmd.IsBoundary);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnRegisterMessageCommand_WhenMessageBoundaryAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task, boundaryMsg], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var activityInstanceId = Guid.NewGuid();
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1", activityInstanceId);

        // Act
        var commands = await task.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var msgCmd = commands.OfType<RegisterMessageCommand>().Single();
        Assert.AreEqual("bm1", msgCmd.ActivityId);
        Assert.AreEqual("msg-def-1", msgCmd.MessageDefinitionId);
        Assert.IsTrue(msgCmd.IsBoundary);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldNotReturnBoundaryCommands_WhenNoBoundariesAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([task], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        var commands = await task.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        Assert.IsFalse(commands.OfType<RegisterTimerCommand>().Any());
        Assert.IsFalse(commands.OfType<RegisterMessageCommand>().Any());
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldOnlyReturnMatchingBoundaryCommands()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var boundaryOnTask1 = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var boundaryOnTask2 = new BoundaryTimerEvent("bt2", "task2", timerDef);
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task1, task2, boundaryOnTask1, boundaryOnTask2], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        var commands = await task1.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var timerCmds = commands.OfType<RegisterTimerCommand>().ToList();
        Assert.HasCount(1, timerCmds);
        Assert.AreEqual("bt1", timerCmds[0].TimerActivityId);
    }

    [TestMethod]
    public async Task ExecuteAsync_CallActivity_ShouldReturnRegisterTimerCommand()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub-process", [], []);
        var timerDef = new TimerDefinition(TimerType.Duration, "PT15M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "call1", timerDef);
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [callActivity, boundaryTimer], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var activityInstanceId = Guid.NewGuid();
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("call1", activityInstanceId);

        // Act
        var commands = await callActivity.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var timerCmd = commands.OfType<RegisterTimerCommand>().Single();
        Assert.AreEqual("bt1", timerCmd.TimerActivityId);
        Assert.AreEqual(TimeSpan.FromMinutes(15), timerCmd.DueTime);
        Assert.IsTrue(timerCmd.IsBoundary);
    }
}
