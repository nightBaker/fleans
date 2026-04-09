using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class ComplexGatewayActivityTests
{
    // ── Fork tests ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Fork_ShouldEmitAddConditionsCommand_ForAllOutgoingConditionalFlows()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-fork", IsFork: true, ActivationCondition: null);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2],
            [
                new ConditionalSequenceFlow("s1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("s2", gateway, end2, "x < 10")
            ]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-fork");

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — should add one AddConditionsCommand with both sequence flows
        var addCmd = commands.OfType<AddConditionsCommand>().FirstOrDefault();
        Assert.IsNotNull(addCmd, "Should emit AddConditionsCommand");
        Assert.AreEqual(2, addCmd.Evaluations.Count);
        CollectionAssert.Contains(addCmd.SequenceFlowIds, "s1");
        CollectionAssert.Contains(addCmd.SequenceFlowIds, "s2");
    }

    [TestMethod]
    public async Task Fork_ShouldCompleteDirectly_WhenNoConditionalFlows()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-fork", IsFork: true, ActivationCondition: null);
        var end1 = new EndEvent("end1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new SequenceFlow("s1", gateway, end1)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-fork");

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        Assert.IsFalse(commands.OfType<AddConditionsCommand>().Any());
        await activityContext.Received().Complete();
    }

    [TestMethod]
    public async Task Fork_GetNextActivities_ShouldReturnAllTrueTargets()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-fork", IsFork: true, ActivationCondition: null);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var end3 = new EndEvent("end3");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2, end3],
            [
                new ConditionalSequenceFlow("s1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("s2", gateway, end2, "x < 10"),
                new ConditionalSequenceFlow("s3", gateway, end3, "x == 5")
            ]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        ActivityTestHelper.SetupConditionStates(workflowContext, activityInstanceId,
            ("s1", true), ("s2", true), ("s3", false));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-fork", activityInstanceId);

        // Act
        var next = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — only the two true targets
        Assert.AreEqual(2, next.Count);
        Assert.IsTrue(next.All(t => t.Token == TokenAction.CreateNew));
        Assert.IsTrue(next.All(t => t.CloneVariables));
    }

    [TestMethod]
    public async Task Fork_GetNextActivities_ShouldReturnDefault_WhenAllFalse()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-fork", IsFork: true, ActivationCondition: null);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2],
            [
                new ConditionalSequenceFlow("s1", gateway, end1, "false"),
                new DefaultSequenceFlow("s2", gateway, end2)
            ]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        ActivityTestHelper.SetupConditionStates(workflowContext, activityInstanceId,
            ("s1", false));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-fork", activityInstanceId);

        // Act
        var next = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — default flow taken
        Assert.AreEqual(1, next.Count);
        Assert.AreEqual("end2", next[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public async Task Fork_GetNextActivities_ShouldThrow_WhenAllFalseAndNoDefault()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-fork", IsFork: true, ActivationCondition: null);
        var end1 = new EndEvent("end1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new ConditionalSequenceFlow("s1", gateway, end1, "false")]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        ActivityTestHelper.SetupConditionStates(workflowContext, activityInstanceId, ("s1", false));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-fork", activityInstanceId);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await gateway.GetNextActivities(workflowContext, activityContext, definition));
    }

    // ── Join without condition tests ────────────────────────────────────────

    [TestMethod]
    public async Task Join_NoCondition_ShouldNotComplete_WhenNotAllTokensArrived()
    {
        // Arrange — 2-way fork, only 1 token arrived
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new ScriptTask("task1", "csharp", "");
        var task2 = new ScriptTask("task2", "csharp", "");
        var gateway = new ComplexGateway("cg-join", IsFork: false, ActivationCondition: null);
        var end = new EndEvent("end");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [fork, task1, task2, gateway, end],
            [
                new SequenceFlow("s1", fork, task1),
                new SequenceFlow("s2", fork, task2),
                new SequenceFlow("s3", task1, gateway),
                new SequenceFlow("s4", task2, gateway),
                new SequenceFlow("s5", gateway, end)
            ]);

        var forkInstanceId = Guid.NewGuid();
        var token1 = Guid.NewGuid();
        var forkState = new GatewayForkState(forkInstanceId, null, Guid.NewGuid());
        forkState.CreatedTokenIds.Add(token1);
        forkState.CreatedTokenIds.Add(Guid.NewGuid()); // token2 not arrived

        var completedTask1Context = Substitute.For<IActivityExecutionContext>();
        completedTask1Context.GetActivityId().Returns(ValueTask.FromResult("task1"));
        completedTask1Context.GetTokenId().Returns(ValueTask.FromResult<Guid?>(token1));

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetCompletedActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>(
                [completedTask1Context]));
        workflowContext.FindForkByToken(token1).Returns(ValueTask.FromResult<GatewayForkState?>(forkState));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-join");

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — should NOT complete since token2 hasn't arrived
        await activityContext.DidNotReceive().Complete();
        Assert.IsFalse(commands.OfType<EvaluateActivationConditionCommand>().Any());
    }

    [TestMethod]
    public async Task Join_NoCondition_GetNextActivities_ShouldReturnRestoreParent()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-join", IsFork: false, ActivationCondition: null);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end],
            [new SequenceFlow("s1", gateway, end)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-join");

        // Act
        var next = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, next.Count);
        Assert.AreEqual(TokenAction.RestoreParent, next[0].Token);
        Assert.AreEqual("end", next[0].NextActivity.ActivityId);
    }

    // ── Join with activation condition tests ────────────────────────────────

    [TestMethod]
    public async Task Join_WithCondition_ShouldCreateJoinState_AndEmitEvaluateCommand_OnFirstToken()
    {
        // Arrange
        var gateway = new ComplexGateway("cg-join", IsFork: false, ActivationCondition: "_context._nroftoken >= 2");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end],
            [new SequenceFlow("s1", gateway, end)]);

        var activityInstanceId = Guid.NewGuid();
        var joinState = new ComplexGatewayJoinState(activityInstanceId, "_context._nroftoken >= 2");

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetComplexGatewayJoinState(activityInstanceId)
            .Returns(ValueTask.FromResult<ComplexGatewayJoinState?>(null));
        workflowContext.GetOrCreateComplexGatewayJoinState(activityInstanceId, "_context._nroftoken >= 2")
            .Returns(ValueTask.FromResult(joinState));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-join", activityInstanceId);

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var evalCmd = commands.OfType<EvaluateActivationConditionCommand>().FirstOrDefault();
        Assert.IsNotNull(evalCmd, "Should emit EvaluateActivationConditionCommand");
        Assert.AreEqual("_context._nroftoken >= 2", evalCmd.Condition);
        Assert.AreEqual(1, evalCmd.NrOfToken); // Increment was called once
    }

    [TestMethod]
    public async Task Join_WithCondition_ShouldDiscardToken_WhenAlreadyFired()
    {
        // Arrange — gateway already fired (HasFired = true), late token arrives
        var gateway = new ComplexGateway("cg-join", IsFork: false, ActivationCondition: "_context._nroftoken >= 2");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end],
            [new SequenceFlow("s1", gateway, end)]);

        var activityInstanceId = Guid.NewGuid();
        var firedJoinState = new ComplexGatewayJoinState(activityInstanceId, "_context._nroftoken >= 2");
        firedJoinState.Increment();
        firedJoinState.Increment();
        firedJoinState.MarkFired();

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetComplexGatewayJoinState(activityInstanceId)
            .Returns(ValueTask.FromResult<ComplexGatewayJoinState?>(firedJoinState));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-join", activityInstanceId);

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — no EvaluateActivationConditionCommand, no Complete
        Assert.IsFalse(commands.OfType<EvaluateActivationConditionCommand>().Any());
        await activityContext.DidNotReceive().Complete();
        workflowContext.DidNotReceive().GetOrCreateComplexGatewayJoinState(
            Arg.Any<Guid>(), Arg.Any<string>());
    }

    [TestMethod]
    public async Task Join_WithCondition_GetNextActivities_ShouldReturnRestoreParent()
    {
        // Join path always returns RestoreParent, same as without condition
        var gateway = new ComplexGateway("cg-join", IsFork: false, ActivationCondition: "_context._nroftoken >= 2");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end],
            [new SequenceFlow("s1", gateway, end)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("cg-join");

        var next = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        Assert.AreEqual(1, next.Count);
        Assert.AreEqual(TokenAction.RestoreParent, next[0].Token);
    }
}
