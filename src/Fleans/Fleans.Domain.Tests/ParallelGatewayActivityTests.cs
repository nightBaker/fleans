using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class ParallelGatewayActivityTests
{
    [TestMethod]
    public async Task Fork_ExecuteAsync_ShouldCallComplete()
    {
        // Arrange
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [fork, task1, task2],
            [
                new SequenceFlow("seq1", fork, task1),
                new SequenceFlow("seq2", fork, task2)
            ]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("fork");

        // Act
        await fork.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert
        await activityContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task Fork_GetNextActivities_ShouldReturnAllOutgoingFlowTargets()
    {
        // Arrange
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var task3 = new TaskActivity("task3");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [fork, task1, task2, task3],
            [
                new SequenceFlow("seq1", fork, task1),
                new SequenceFlow("seq2", fork, task2),
                new SequenceFlow("seq3", fork, task3)
            ]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("fork");

        // Act
        var nextActivities = await fork.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(3, nextActivities);
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "task1"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "task2"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "task3"));
    }

    [TestMethod]
    public async Task Join_GetNextActivities_ShouldReturnSingleOutgoingFlow()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var join = new ParallelGateway("join", IsFork: false);
        var end = new EndEvent("end");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task1, join, end],
            [
                new SequenceFlow("seq1", task1, join),
                new SequenceFlow("seq2", join, end)
            ]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("join");

        // Act
        var nextActivities = await join.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task Join_ExecuteAsync_ShouldCallComplete_WhenAllPathsCompleted()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var join = new ParallelGateway("join", IsFork: false);
        var end = new EndEvent("end");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task1, task2, join, end],
            [
                new SequenceFlow("seq1", task1, join),
                new SequenceFlow("seq2", task2, join),
                new SequenceFlow("seq3", join, end)
            ]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        // Both incoming tasks are completed
        var task1Context = Substitute.For<IActivityExecutionContext>();
        task1Context.GetActivityId().Returns(ValueTask.FromResult("task1"));
        task1Context.IsCompleted().Returns(ValueTask.FromResult(true));

        var task2Context = Substitute.For<IActivityExecutionContext>();
        task2Context.GetActivityId().Returns(ValueTask.FromResult("task2"));
        task2Context.IsCompleted().Returns(ValueTask.FromResult(true));

        workflowContext.GetCompletedActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>(
                new List<IActivityExecutionContext> { task1Context, task2Context }));
        workflowContext.GetActiveActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>(
                new List<IActivityExecutionContext> { task1Context, task2Context }));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("join");

        // Act
        await join.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert — join calls Complete because all paths are done
        await activityContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task Join_ExecuteAsync_ShouldCallExecute_WhenNotAllPathsCompleted()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var join = new ParallelGateway("join", IsFork: false);
        var end = new EndEvent("end");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task1, task2, join, end],
            [
                new SequenceFlow("seq1", task1, join),
                new SequenceFlow("seq2", task2, join),
                new SequenceFlow("seq3", join, end)
            ]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        // task1 is completed but task2 is still active
        var task1Context = Substitute.For<IActivityExecutionContext>();
        task1Context.GetActivityId().Returns(ValueTask.FromResult("task1"));
        task1Context.IsCompleted().Returns(ValueTask.FromResult(false));

        var task2Context = Substitute.For<IActivityExecutionContext>();
        task2Context.GetActivityId().Returns(ValueTask.FromResult("task2"));
        task2Context.IsCompleted().Returns(ValueTask.FromResult(false));

        workflowContext.GetCompletedActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
        workflowContext.GetActiveActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>(
                new List<IActivityExecutionContext> { task1Context }));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("join");

        // Act
        await join.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert — join calls Execute (not Complete) because not all paths are done
        await activityContext.Received().Execute();
    }
}
