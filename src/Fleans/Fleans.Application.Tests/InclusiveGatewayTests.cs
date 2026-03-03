using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class InclusiveGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task InclusiveGateway_TwoOfThreeTrue_BothBranchesExecuteAndJoin()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — evaluate conditions: task1=true, task2=true, task3=false
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask1", true);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask2", true);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask3", false);

        // Assert — task1 and task2 should be active
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);

        var activeTaskActivities = snapshot.ActiveActivities
            .Where(a => a.ActivityType == "TaskActivity").ToList();
        Assert.AreEqual(2, activeTaskActivities.Count);
        Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task1"));
        Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task2"));

        // Act — complete both tasks
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());
        await workflowInstance.CompleteActivity("task2", new ExpandoObject());

        // Assert — workflow should be completed
        snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed after both active branches done");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task InclusiveGateway_OneOfThreeTrue_SingleBranchAndJoinProceeds()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — only task1 condition is true
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask1", true);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask2", false);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask3", false);

        // Assert — only task1 should be active
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);

        var activeTaskActivities = snapshot.ActiveActivities
            .Where(a => a.ActivityType == "TaskActivity").ToList();
        Assert.AreEqual(1, activeTaskActivities.Count);
        Assert.AreEqual("task1", activeTaskActivities[0].ActivityId);

        // Act — complete task1
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — join should proceed, workflow completes
        snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete after the single active branch is done");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task InclusiveGateway_JoinShouldNotComplete_UntilAllActiveBranchesDone()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — two conditions true, one false
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask1", true);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask2", true);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask3", false);

        // Complete only task1 (task2 still pending)
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — workflow should NOT be completed yet
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed — task2 still pending");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task2"),
            "task2 should still be active");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should NOT have been reached");
    }

    [TestMethod]
    public async Task InclusiveGateway_AllConditionsFalse_TakesDefaultFlow()
    {
        // Arrange
        var workflow = CreateInclusiveWithDefaultFlow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — all conditions false
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask1", false);
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask2", false);

        // Assert — workflow completed via endDefault (default flow)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete via default flow");

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "endDefault");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "task1");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "task2");
    }

    [TestMethod]
    public async Task InclusiveGateway_NoShortCircuit_WaitsForAllConditions()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — only first condition is true, other two not yet evaluated
        await workflowInstance.CompleteConditionSequence("fork", "seqForkTask1", true);

        // Assert — fork should NOT have transitioned yet (unlike ExclusiveGateway)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);

        // The fork gateway should still be active — no tasks spawned yet
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "fork"),
            "Fork gateway should still be active — inclusive gateway waits for all conditions");
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityType == "TaskActivity"),
            "No task activities should be active yet — all conditions must be evaluated first");
    }

    private static IWorkflowDefinition CreateInclusiveThreeBranchWorkflow()
    {
        var start = new StartEvent("start");
        var fork = new InclusiveGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var task3 = new TaskActivity("task3");
        var join = new InclusiveGateway("join", IsFork: false);
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "inclusive-three-branch",
            Activities = new List<Activity> { start, fork, task1, task2, task3, join, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seqStartFork", start, fork),
                new ConditionalSequenceFlow("seqForkTask1", fork, task1, "condition1"),
                new ConditionalSequenceFlow("seqForkTask2", fork, task2, "condition2"),
                new ConditionalSequenceFlow("seqForkTask3", fork, task3, "condition3"),
                new SequenceFlow("seqTask1Join", task1, join),
                new SequenceFlow("seqTask2Join", task2, join),
                new SequenceFlow("seqTask3Join", task3, join),
                new SequenceFlow("seqJoinEnd", join, end)
            }
        };
    }

    private static IWorkflowDefinition CreateInclusiveWithDefaultFlow()
    {
        var start = new StartEvent("start");
        var fork = new InclusiveGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var endDefault = new EndEvent("endDefault");

        return new WorkflowDefinition
        {
            WorkflowId = "inclusive-with-default",
            Activities = new List<Activity> { start, fork, task1, task2, endDefault },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seqStartFork", start, fork),
                new ConditionalSequenceFlow("seqForkTask1", fork, task1, "condition1"),
                new ConditionalSequenceFlow("seqForkTask2", fork, task2, "condition2"),
                new DefaultSequenceFlow("seqForkDefault", fork, endDefault)
            }
        };
    }
}
