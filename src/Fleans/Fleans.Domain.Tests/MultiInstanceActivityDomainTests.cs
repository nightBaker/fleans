using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class MultiInstanceActivityDomainTests
{
    // -------------------------------------------------------------------------
    // Constructor validation
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Constructor_WithLoopCardinality_ShouldNotThrow()
    {
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, LoopCardinality: 3);

        Assert.AreEqual("mi1", mi.ActivityId);
        Assert.AreEqual(3, mi.LoopCardinality);
        Assert.AreEqual(inner, mi.InnerActivity);
    }

    [TestMethod]
    public void Constructor_WithInputCollection_ShouldNotThrow()
    {
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, InputCollection: "items");

        Assert.AreEqual("items", mi.InputCollection);
        Assert.IsNull(mi.LoopCardinality);
    }

    [TestMethod]
    public void Constructor_WithNeitherCardinalityNorCollection_ShouldThrow()
    {
        var inner = new TaskActivity("inner");
        Assert.ThrowsExactly<ArgumentException>(
            () => new MultiInstanceActivity("mi1", inner));
    }

    [TestMethod]
    public void Constructor_WithNegativeLoopCardinality_ShouldThrow()
    {
        var inner = new TaskActivity("inner");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new MultiInstanceActivity("mi1", inner, LoopCardinality: -1));
    }

    // -------------------------------------------------------------------------
    // ExecuteAsync — MI host with LoopCardinality
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task ExecuteAsync_AsHost_WithLoopCardinality_ShouldSpawnAllIterations()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, LoopCardinality: 3);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [mi, end],
            [new SequenceFlow("seq1", mi, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("mi1");

        var hostInstanceId = Guid.NewGuid();
        activityContext.GetActivityInstanceId().Returns(ValueTask.FromResult(hostInstanceId));
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(null));
        activityContext.SetMultiInstanceTotal(Arg.Any<int>()).Returns(ValueTask.CompletedTask);

        // Act
        var commands = await mi.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — 3 spawn commands, one per iteration
        var spawnCmds = commands.OfType<SpawnActivityCommand>().ToList();
        Assert.HasCount(3, spawnCmds);
        Assert.AreEqual(0, spawnCmds[0].MultiInstanceIndex);
        Assert.AreEqual(1, spawnCmds[1].MultiInstanceIndex);
        Assert.AreEqual(2, spawnCmds[2].MultiInstanceIndex);
        await activityContext.Received(1).SetMultiInstanceTotal(3);
    }

    [TestMethod]
    public async Task ExecuteAsync_AsHost_Sequential_ShouldSpawnOnlyFirstIteration()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, IsSequential: true, LoopCardinality: 4);
        var definition = ActivityTestHelper.CreateWorkflowDefinition([mi], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("mi1");

        activityContext.GetActivityInstanceId().Returns(ValueTask.FromResult(Guid.NewGuid()));
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(null));
        activityContext.SetMultiInstanceTotal(Arg.Any<int>()).Returns(ValueTask.CompletedTask);

        // Act
        var commands = await mi.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — only 1 spawn command for sequential mode
        var spawnCmds = commands.OfType<SpawnActivityCommand>().ToList();
        Assert.HasCount(1, spawnCmds);
        Assert.AreEqual(0, spawnCmds[0].MultiInstanceIndex);
        await activityContext.Received(1).SetMultiInstanceTotal(4);
    }

    [TestMethod]
    public async Task ExecuteAsync_AsHost_WithInputCollection_ShouldSpawnIterationsWithItems()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner,
            InputCollection: "items", InputDataItem: "item");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([mi], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("mi1");

        var variablesId = Guid.NewGuid();
        activityContext.GetActivityInstanceId().Returns(ValueTask.FromResult(Guid.NewGuid()));
        activityContext.GetVariablesStateId().Returns(ValueTask.FromResult(variablesId));
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(null));
        activityContext.SetMultiInstanceTotal(Arg.Any<int>()).Returns(ValueTask.CompletedTask);

        var collectionItems = new List<object> { "a", "b", "c" };
        workflowContext.GetVariable(variablesId, "items")
            .Returns(ValueTask.FromResult<object?>(collectionItems));

        // Act
        var commands = await mi.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — 3 spawn commands, each carrying its iteration item
        var spawnCmds = commands.OfType<SpawnActivityCommand>().ToList();
        Assert.HasCount(3, spawnCmds);
        Assert.AreEqual("a", spawnCmds[0].IterationItem);
        Assert.AreEqual("b", spawnCmds[1].IterationItem);
        Assert.AreEqual("c", spawnCmds[2].IterationItem);
        Assert.AreEqual("item", spawnCmds[0].IterationItemName);
        await activityContext.Received(1).SetMultiInstanceTotal(3);
    }

    [TestMethod]
    public async Task ExecuteAsync_AsHost_WithNonListInputCollection_ShouldThrow()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, InputCollection: "notAList");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([mi], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("mi1");

        var variablesId = Guid.NewGuid();
        activityContext.GetVariablesStateId().Returns(ValueTask.FromResult(variablesId));
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(null));
        workflowContext.GetVariable(variablesId, "notAList")
            .Returns(ValueTask.FromResult<object?>("not-a-list"));

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => mi.ExecuteAsync(workflowContext, activityContext, definition));
    }

    // -------------------------------------------------------------------------
    // ExecuteAsync — MI iteration delegates to inner activity
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task ExecuteAsync_AsIteration_ShouldDelegateToInnerActivity()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, LoopCardinality: 3);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [mi, end],
            [new SequenceFlow("seq1", mi, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("mi1");

        // Simulate an iteration context (index is set)
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(0));

        // Act
        var commands = await mi.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — delegates to inner TaskActivity behaviour (inner publishes its own id, no spawning)
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("inner", executedEvent.activityId);
        Assert.AreEqual("TaskActivity", executedEvent.TypeName);
        Assert.IsFalse(commands.OfType<SpawnActivityCommand>().Any());
    }

    // -------------------------------------------------------------------------
    // GetNextActivities
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task GetNextActivities_AsHost_ShouldReturnNextFlow()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, LoopCardinality: 2);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [mi, end],
            [new SequenceFlow("seq1", mi, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("mi1");
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(null));

        // Act
        var nextActivities = await mi.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_AsIteration_ShouldReturnEmpty()
    {
        // Arrange
        var inner = new TaskActivity("inner");
        var mi = new MultiInstanceActivity("mi1", inner, LoopCardinality: 2);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [mi, end],
            [new SequenceFlow("seq1", mi, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("mi1");
        activityContext.GetMultiInstanceIndex().Returns(ValueTask.FromResult<int?>(0));

        // Act
        var nextActivities = await mi.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — iterations do not transition; completion is handled by scope
        Assert.HasCount(0, nextActivities);
    }
}
