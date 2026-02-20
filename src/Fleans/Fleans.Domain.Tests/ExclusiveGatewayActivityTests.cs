using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class ExclusiveGatewayActivityTests
{
    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTrueConditionTarget()
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
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        // Set up condition state: seq1 is true
        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] =
            [
                CreateEvaluatedConditionState("seq1", activityInstanceId, true),
                CreateEvaluatedConditionState("seq2", activityInstanceId, false)
            ]
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end1", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnDefaultFlow_WhenNoTrueCondition()
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

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("endDefault", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldThrow_WhenNoTrueConditionAndNoDefaultFlow()
    {
        // Arrange
        var gateway = new ExclusiveGateway("if");
        var end1 = new EndEvent("end1");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, end1],
            [new ConditionalSequenceFlow("seq1", gateway, end1, "x > 0")]);

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);

        // All conditions false, no default flow
        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] =
            [
                CreateEvaluatedConditionState("seq1", activityInstanceId, false)
            ]
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));

        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gateway.GetNextActivities(workflowContext, activityContext));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldAddConditionalSequences_AndQueueEvaluateEvents()
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
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("if", activityInstanceId);

        // Act
        await gateway.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert — should have added condition sequences to workflow
        await workflowContext.Received(1).AddConditionSequenceStates(
            activityInstanceId,
            Arg.Is<string[]>(ids => ids.Length == 2 && ids.Contains("seq1") && ids.Contains("seq2")));

        // Should have published evaluate events (plus one WorkflowActivityExecutedEvent from base)
        var evaluateEvents = publishedEvents.OfType<EvaluateConditionEvent>().ToList();
        Assert.HasCount(2, evaluateEvents);
        Assert.IsTrue(evaluateEvents.Any(e => e.SequenceFlowId == "seq1" && e.Condition == "x > 0"));
        Assert.IsTrue(evaluateEvents.Any(e => e.SequenceFlowId == "seq2" && e.Condition == "x < 0"));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldAutoComplete_WhenNoConditionalSequencesExist()
    {
        // Arrange — gateway with only a default flow (no conditional sequences)
        var gateway = new ExclusiveGateway("if");
        var endDefault = new EndEvent("endDefault");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, endDefault],
            [new DefaultSequenceFlow("seqDefault", gateway, endDefault)]);

        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("if");

        // Act
        await gateway.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert — should auto-complete since no conditions to evaluate
        await activityContext.Received(1).Complete();
    }

    private static ConditionSequenceState CreateEvaluatedConditionState(
        string sequenceFlowId, Guid gatewayInstanceId, bool result)
    {
        var state = new ConditionSequenceState(sequenceFlowId, gatewayInstanceId, Guid.Empty);
        state.SetResult(result);
        return state;
    }
}
