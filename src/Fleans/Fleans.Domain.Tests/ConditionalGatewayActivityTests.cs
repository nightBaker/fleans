using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class ConditionalGatewayActivityTests
{
    [TestMethod]
    public async Task SetConditionResult_ShouldReturnTrue_WhenResultIsTrue()
    {
        // Arrange
        var gateway = new ExclusiveGateway("if");
        var end1 = new EndEvent("end1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0")]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act
        var result = await gateway.SetConditionResult(workflowContext, activityContext, "seq1", true, definition);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldReturnFalse_WhenResultIsFalseAndConditionsStillPending()
    {
        // Arrange
        var gateway = new ExclusiveGateway("if");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, end2],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new ConditionalSequenceFlow("seq2", gateway, end2, "x < 0")
            ]);

        var activityInstanceId = Guid.NewGuid();

        // seq1 evaluated as false, seq2 not yet evaluated
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(false);
        var seq2State = new ConditionSequenceState("seq2", activityInstanceId, Guid.Empty);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State, seq2State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act
        var result = await gateway.SetConditionResult(workflowContext, activityContext, "seq1", false, definition);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldReturnTrue_WhenAllConditionsFalseAndDefaultFlowExists()
    {
        // Arrange
        var gateway = new ExclusiveGateway("if");
        var end1 = new EndEvent("end1");
        var endDefault = new EndEvent("endDefault");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1, endDefault],
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0"),
                new DefaultSequenceFlow("seqDefault", gateway, endDefault)
            ]);

        var activityInstanceId = Guid.NewGuid();

        // seq1 already evaluated as false
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(false);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act
        var result = await gateway.SetConditionResult(workflowContext, activityContext, "seq1", false, definition);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldThrow_WhenAllConditionsFalseAndNoDefaultFlow()
    {
        // Arrange
        var gateway = new ExclusiveGateway("if");
        var end1 = new EndEvent("end1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0")]);

        var activityInstanceId = Guid.NewGuid();

        // seq1 already evaluated as false
        var seq1State = new ConditionSequenceState("seq1", activityInstanceId, Guid.Empty);
        seq1State.SetResult(false);

        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = [seq1State]
        };

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gateway.SetConditionResult(workflowContext, activityContext, "seq1", false, definition));
    }
}
