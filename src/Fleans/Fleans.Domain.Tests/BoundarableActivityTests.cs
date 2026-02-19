using Fleans.Domain.Activities;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundarableActivityTests
{
    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldRegisterTimerReminder_WhenBoundaryTimerAttached()
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
        await task.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterTimerReminder(
            activityInstanceId, "bt1", TimeSpan.FromMinutes(10));
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldRegisterMessageSubscription_WhenMessageBoundaryAttached()
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
        await task.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterBoundaryMessageSubscription(
            activityInstanceId, "bm1", "msg-def-1");
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldNotRegister_WhenNoBoundariesAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([task], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        await task.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.DidNotReceive().RegisterTimerReminder(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
        await workflowContext.DidNotReceive().RegisterBoundaryMessageSubscription(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldOnlyRegisterMatchingBoundaries()
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
        await task1.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterTimerReminder(
            Arg.Any<Guid>(), "bt1", Arg.Any<TimeSpan>());
        await workflowContext.DidNotReceive().RegisterTimerReminder(
            Arg.Any<Guid>(), "bt2", Arg.Any<TimeSpan>());
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_CallActivity_ShouldRegisterBoundaryTimer()
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
        await callActivity.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterTimerReminder(
            activityInstanceId, "bt1", TimeSpan.FromMinutes(15));
    }
}
