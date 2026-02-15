using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class ExclusiveGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeTrueBranch_WhenFirstConditionIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition true, gateway should auto-complete
        await workflowInstance.CompleteConditionSequence("if", "seq2", true);

        // Assert — workflow completed via end1 (true branch)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldShortCircuit_OnFirstTrueCondition()
    {
        // Arrange — gateway with two conditional flows, first returns true
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition is true -> should complete immediately
        // Second condition is never evaluated
        await workflowInstance.CompleteConditionSequence("if", "seq2", true);

        // Assert — workflow completed without needing seq3
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeSecondBranch_WhenFirstIsFalseSecondIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false, second true
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);
        await workflowInstance.CompleteConditionSequence("if", "seq3", true);

        // Assert — workflow completed via end2
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
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
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — all conditions false
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);
        await workflowInstance.CompleteConditionSequence("if", "seq3", false);

        // Assert — workflow completed via endDefault (default flow)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "endDefault");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldThrow_WhenAllConditionsFalse_AndNoDefaultFlow()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches(); // no default flow
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);

        // Assert — second condition false should throw
        // Orleans may wrap the exception, so we catch the broadest type
        var threw = false;
        try
        {
            await workflowInstance.CompleteConditionSequence("if", "seq3", false);
        }
        catch (Exception ex)
        {
            threw = true;
            // The InvalidOperationException may be thrown directly or wrapped by Orleans
            var innerMost = GetInnermostException(ex);
            Assert.IsInstanceOfType<InvalidOperationException>(innerMost,
                $"Expected InvalidOperationException but got {innerMost.GetType().Name}: {innerMost.Message}");
        }

        Assert.IsTrue(threw, "Expected an exception when all conditions are false with no default flow");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldPassThrough_WhenOnlyDefaultFlowExists()
    {
        // Arrange — gateway with no conditional flows, only a default flow
        var workflow = CreateWorkflowWithOnlyDefaultFlow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act — start workflow, gateway should auto-complete immediately
        await workflowInstance.StartWorkflow();

        // Assert — workflow completed via endDefault
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "start");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "if");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "endDefault");

        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldNotAutoComplete_WhenConditionsStillPending()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false, second not yet evaluated
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);

        // Assert — workflow not completed, gateway still active
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);

        Assert.IsTrue(snapshot.ActiveActivities.Count > 0);
    }

    private static Exception GetInnermostException(Exception ex)
    {
        while (ex.InnerException is not null)
            ex = ex.InnerException;
        return ex;
    }

    private static IWorkflowDefinition CreateWorkflowWithTwoBranches()
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
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition")
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
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "condition1"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "condition2"),
                new DefaultSequenceFlow("seqDefault", ifActivity, endDefault)
            ]
        };
    }
}
