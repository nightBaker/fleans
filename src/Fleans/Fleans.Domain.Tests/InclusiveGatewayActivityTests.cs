using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class InclusiveGatewayActivityTests
{
    [TestMethod]
    public async Task SetConditionResult_ShouldNotShortCircuit_WhenFirstConditionIsTrue()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("seq2", gateway, end2, "x < 10")
            ]);

        var activityInstanceId = Guid.NewGuid();

        // seq1 evaluated as true, seq2 not yet evaluated
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(true);
        var seq2State = new ConditionSequenceState("seq2", activityInstanceId, Guid.Empty);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State, seq2State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act
        var result = await gateway.SetConditionResult(workflowContext, activityContext, "seq1", true, definition);

        // Assert — should NOT short-circuit; must wait for all conditions
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldReturnTrue_WhenAllConditionsEvaluated()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("seq2", gateway, end2, "x < 10")
            ]);

        var activityInstanceId = Guid.NewGuid();

        // Both conditions evaluated: seq1=true, seq2=false
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(true);
        var seq2State = new ConditionSequenceState("seq2", activityInstanceId, Guid.Empty);
        seq2State.SetResult(false);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State, seq2State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act
        var result = await gateway.SetConditionResult(workflowContext, activityContext, "seq2", false, definition);

        // Assert — all evaluated, at least one true
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnAllTrueTargets()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var end3 = new EndEvent("end3");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2, end3],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("seq2", gateway, end2, "x < 10"),
                new ConditionalSequenceFlow("seq3", gateway, end3, "x == 5")
            ]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        // seq1=true, seq2=true, seq3=false
        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] =
            [
                CreateEvaluatedConditionState("seq1", activityInstanceId, true),
                CreateEvaluatedConditionState("seq2", activityInstanceId, true),
                CreateEvaluatedConditionState("seq3", activityInstanceId, false)
            ]
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — should return both true targets, not just the first
        Assert.AreEqual(2, nextActivities.Count);
        Assert.IsTrue(nextActivities.Any(a => a.NextActivity.ActivityId == "end1"));
        Assert.IsTrue(nextActivities.Any(a => a.NextActivity.ActivityId == "end2"));
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnDefault_WhenAllConditionsFalse()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var endDefault = new EndEvent("endDefault");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, endDefault],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new DefaultSequenceFlow("seqDefault", gateway, endDefault)
            ]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        // All conditions false
        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] =
            [
                CreateEvaluatedConditionState("seq1", activityInstanceId, false)
            ]
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("endDefault", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldThrow_WhenAllConditionsFalseAndNoDefaultFlow()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0")]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] =
            [
                CreateEvaluatedConditionState("seq1", activityInstanceId, false)
            ]
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gateway.GetNextActivities(workflowContext, activityContext, definition));
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldReturnTrue_WhenAllConditionsFalseAndDefaultFlowExists()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var endDefault = new EndEvent("endDefault");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, endDefault],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new DefaultSequenceFlow("seqDefault", gateway, endDefault)
            ]);

        var activityInstanceId = Guid.NewGuid();

        // seq1 evaluated as false (only conditional sequence)
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(false);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act
        var result = await gateway.SetConditionResult(workflowContext, activityContext, "seq1", false, definition);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldThrow_WhenAllConditionsFalseAndNoDefaultFlow()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0")]);

        var activityInstanceId = Guid.NewGuid();

        // seq1 evaluated as false
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(false);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gateway.SetConditionResult(workflowContext, activityContext, "seq1", false, definition));
    }

    [TestMethod]
    public async Task ExecuteAsync_Fork_ShouldAddConditionalSequences()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("seq2", gateway, end2, "x < 10")
            ]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork", activityInstanceId);

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        var condCmd = commands.OfType<AddConditionsCommand>().Single();
        Assert.AreEqual(2, condCmd.SequenceFlowIds.Length);
        Assert.IsTrue(condCmd.SequenceFlowIds.Contains("seq1"));
        Assert.IsTrue(condCmd.SequenceFlowIds.Contains("seq2"));
        Assert.AreEqual(2, condCmd.Evaluations.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_Fork_ShouldAutoComplete_WhenNoConditionalSequences()
    {
        // Arrange — fork with only a default flow
        var gateway = new InclusiveGateway("inclusive-fork", IsFork: true);
        var endDefault = new EndEvent("endDefault");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, endDefault],
            [new DefaultSequenceFlow("seqDefault", gateway, endDefault)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-fork");

        // Act
        await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task GetNextActivities_Join_ShouldReturnAllOutgoingFlows()
    {
        // Arrange
        var gateway = new InclusiveGateway("inclusive-join", IsFork: false);
        var end1 = new EndEvent("end1");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new SequenceFlow("seq-out", gateway, end1)]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("inclusive-join", activityInstanceId);

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("end1", nextActivities[0].NextActivity.ActivityId);
    }

    private static ConditionSequenceState CreateEvaluatedConditionState(
        string sequenceFlowId, Guid gatewayInstanceId, bool result)
    {
        var state = new ConditionSequenceState(sequenceFlowId, gatewayInstanceId, Guid.Empty);
        state.SetResult(result);
        return state;
    }
}
