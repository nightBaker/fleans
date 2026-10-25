using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;

namespace Fleans.Application.Tests;

/// <summary>
/// The gateway's conditions are evaluated in the instance grain, within the call that reaches the
/// gateway — so the outcome is visible as soon as <c>StartWorkflow</c> returns. The test silo's
/// condition evaluator returns <c>true</c> only for the expression <c>"true"</c>.
/// </summary>
[TestClass]
public class ExclusiveGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeTrueBranch_WhenFirstConditionIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches(seq2: "true", seq3: "false");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await workflowInstance.StartWorkflow(workflow);

        // Assert — completed via end1 (true branch) without waiting for anything
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldShortCircuit_OnFirstTrueCondition()
    {
        // Arrange — both conditions true: only the first is evaluated
        var workflow = CreateWorkflowWithTwoBranches(seq2: "true", seq3: "true");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await workflowInstance.StartWorkflow(workflow);

        // Assert — end1 taken, the second flow never evaluated
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
        var seq3 = snapshot.ConditionSequences.Single(c => c.SequenceFlowId == "seq3");
        Assert.IsFalse(seq3.Result);
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeSecondBranch_WhenFirstIsFalseSecondIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches(seq2: "false", seq3: "true");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await workflowInstance.StartWorkflow(workflow);

        // Assert — completed via end2
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end2");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end1");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeDefaultFlow_WhenAllConditionsAreFalse()
    {
        // Arrange
        var workflow = CreateWorkflowWithDefaultFlow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await workflowInstance.StartWorkflow(workflow);

        // Assert — completed via endDefault (default flow)
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "endDefault");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldFailTheGateway_WhenAllConditionsFalse_AndNoDefaultFlow()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches(seq2: "false", seq3: "false"); // no default flow
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await workflowInstance.StartWorkflow(workflow);

        // Assert — the gateway failed (no flow to take), which fails the instance (#762)
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsFailed);
        var gateway = snapshot.CompletedActivities.Single(a => a.ActivityId == "if");
        Assert.IsNotNull(gateway.ErrorState);
        StringAssert.Contains(gateway.ErrorState.Message, "default flow");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldFailTheGateway_WhenAConditionThrows()
    {
        // Arrange — a condition the real evaluator cannot parse
        var workflow = CreateWorkflowWithTwoBranches(seq2: "THROW", seq3: "true");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await workflowInstance.StartWorkflow(workflow);

        // Assert — the gateway failed with the evaluator's error; no branch was taken
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsFailed);
        var gateway = snapshot.CompletedActivities.Single(a => a.ActivityId == "if");
        Assert.IsNotNull(gateway.ErrorState);
        StringAssert.Contains(gateway.ErrorState.Message, "Simulated condition failure");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldPassThrough_WhenOnlyDefaultFlowExists()
    {
        // Arrange — gateway with no conditional flows, only a default flow
        var workflow = CreateWorkflowWithOnlyDefaultFlow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act — start workflow, gateway should auto-complete immediately
        await workflowInstance.StartWorkflow(workflow);

        // Assert — workflow completed via endDefault
        var snapshot = await QueryService.GetStateSnapshot(workflowInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "start");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "if");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "endDefault");

        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }

    private static IWorkflowDefinition CreateWorkflowWithTwoBranches(string seq2, string seq3)
    {
        var start = new StartEvent("start");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow1",
            Activities = [start, ifActivity, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new ConditionalSequenceFlow("seq2", ifActivity, end1, seq2),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, seq3)
            ]
        };
    }

    private static IWorkflowDefinition CreateWorkflowWithOnlyDefaultFlow()
    {
        var start = new StartEvent("start");
        var endDefault = new EndEvent("endDefault");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow-only-default",
            Activities = [start, ifActivity, endDefault],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new DefaultSequenceFlow("seqDefault", ifActivity, endDefault)
            ]
        };
    }

    private static IWorkflowDefinition CreateWorkflowWithDefaultFlow()
    {
        var start = new StartEvent("start");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var endDefault = new EndEvent("endDefault");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow-default",
            Activities = [start, ifActivity, end1, end2, endDefault],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "false"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "false"),
                new DefaultSequenceFlow("seqDefault", ifActivity, endDefault)
            ]
        };
    }
}
