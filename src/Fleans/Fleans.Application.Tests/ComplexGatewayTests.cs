using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class ComplexGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ComplexGatewayFork_ConditionalBranches_ShouldActivateMatchingPaths()
    {
        // Arrange — fork with two true conditions, one false
        var workflow = CreateComplexForkWorkflow(
            task1Condition: "true", task2Condition: "true", task3Condition: "false");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Wait for condition evaluator to process fork conditions (need both true branches)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Count(a => a.ActivityType == "TaskActivity") >= 2);

        // Assert — task1 and task2 active, task3 not
        var activeTaskActivities = snapshot.ActiveActivities
            .Where(a => a.ActivityType == "TaskActivity").ToList();
        Assert.AreEqual(2, activeTaskActivities.Count,
            $"Expected 2 active tasks, got: {string.Join(", ", activeTaskActivities.Select(a => a.ActivityId))}");
        Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task1"));
        Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task2"));
    }

    [TestMethod]
    public async Task ComplexGatewayFork_AllFalse_ShouldTakeDefaultFlow()
    {
        // Arrange — all conditions false, default flow leads to endDefault
        var workflow = CreateComplexForkWithDefault(
            task1Condition: "false", task2Condition: "false");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "defaultTask"));

        // Assert — default path taken
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "defaultTask"));
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityId == "task1"));
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityId == "task2"));
    }

    [TestMethod]
    public async Task ComplexGatewayJoin_WithActivationCondition_ShouldFireOnFirstToken()
    {
        // Arrange — parallel fork → two tasks → complex join with condition "true"
        // SimpleConditionEvaluator treats "true" as always-true, so the gateway fires
        // on the very first token (simulating an _nroftoken >= 1 scenario).
        var workflow = CreateParallelForkComplexJoinWorkflow(activationCondition: "true");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        await WaitForCondition(instanceId,
            s => s.ActiveActivities.Count(a => a.ActivityType == "TaskActivity") >= 2);

        // Act — complete only task1; the activation condition evaluates "true" → gateway fires
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — join fires after first token, afterJoin becomes active
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "afterJoin"), timeoutMs: 15000);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "join"),
            "Join should have completed after first token");

        // Complete afterJoin to finish the workflow
        await workflowInstance.CompleteActivity("afterJoin", new ExpandoObject());

        // Complete late-arriving task2 — should be silently discarded
        await workflowInstance.CompleteActivity("task2", new ExpandoObject());

        snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task ComplexGatewayJoin_WithFalseCondition_ShouldNotFire()
    {
        // Arrange — parallel fork → two tasks → complex join with condition "false"
        // The gateway should never fire because the condition is always false
        var workflow = CreateParallelForkComplexJoinWorkflow(activationCondition: "false");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        await WaitForCondition(instanceId,
            s => s.ActiveActivities.Count(a => a.ActivityType == "TaskActivity") >= 2);

        // Act — complete both tasks
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());
        await workflowInstance.CompleteActivity("task2", new ExpandoObject());

        // Assert — join should NOT fire (condition always false), afterJoin never reached
        // Wait for both tasks to complete (confirming condition evaluation has been attempted)
        await WaitForCondition(instanceId,
            s => s.CompletedActivities.Count(a => a.ActivityId is "task1" or "task2") >= 2);
        await Task.Delay(200); // allow time for any async side effects after condition evaluation
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted,
            "Workflow should not complete — activation condition is always false");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "afterJoin"),
            "afterJoin should not be reached when activation condition is false");
    }

    [TestMethod]
    public async Task ComplexGatewayJoin_WithoutActivationCondition_ShouldWaitForAllTokens()
    {
        // Arrange — parallel fork → two tasks → complex join WITHOUT activation condition
        // Should mirror ParallelGateway join: wait for all tokens
        var workflow = CreateParallelForkComplexJoinWorkflow(activationCondition: null);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        await WaitForCondition(instanceId,
            s => s.ActiveActivities.Count(a => a.ActivityType == "TaskActivity") >= 2);

        // Act — complete only task1
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — join not yet fired (needs all tokens)
        await WaitForCondition(instanceId,
            s => s.CompletedActivities.Any(a => a.ActivityId == "task1"));
        await Task.Delay(100); // allow time for any async side effects
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "join"),
            "Join without condition should wait for all tokens");

        // Act — complete task2
        await workflowInstance.CompleteActivity("task2", new ExpandoObject());

        // Assert — join fires, afterJoin becomes active
        snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "afterJoin"));
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "join"),
            "Join should have completed after all tokens arrived");

        // Complete afterJoin
        await workflowInstance.CompleteActivity("afterJoin", new ExpandoObject());

        snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }



    private static IWorkflowDefinition CreateComplexForkWorkflow(
        string task1Condition, string task2Condition, string task3Condition)
    {
        var start = new StartEvent("start");
        var fork = new ComplexGateway("fork", IsFork: true, ActivationCondition: null);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var task3 = new TaskActivity("task3");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "complex-fork-test",
            Activities = new List<Activity> { start, fork, task1, task2, task3, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("s0", start, fork),
                new ConditionalSequenceFlow("s1", fork, task1, task1Condition),
                new ConditionalSequenceFlow("s2", fork, task2, task2Condition),
                new ConditionalSequenceFlow("s3", fork, task3, task3Condition),
                new SequenceFlow("s4", task1, end),
                new SequenceFlow("s5", task2, end),
                new SequenceFlow("s6", task3, end)
            }
        };
    }

    private static IWorkflowDefinition CreateComplexForkWithDefault(
        string task1Condition, string task2Condition)
    {
        var start = new StartEvent("start");
        var fork = new ComplexGateway("fork", IsFork: true, ActivationCondition: null);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var defaultTask = new TaskActivity("defaultTask");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "complex-fork-default-test",
            Activities = new List<Activity> { start, fork, task1, task2, defaultTask, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("s0", start, fork),
                new ConditionalSequenceFlow("s1", fork, task1, task1Condition),
                new ConditionalSequenceFlow("s2", fork, task2, task2Condition),
                new DefaultSequenceFlow("s3", fork, defaultTask),
                new SequenceFlow("s4", task1, end),
                new SequenceFlow("s5", task2, end),
                new SequenceFlow("s6", defaultTask, end)
            }
        };
    }

    private static IWorkflowDefinition CreateParallelForkComplexJoinWorkflow(string? activationCondition)
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var join = new ComplexGateway("join", IsFork: false, ActivationCondition: activationCondition);
        var afterJoin = new TaskActivity("afterJoin");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "complex-join-test",
            Activities = new List<Activity> { start, fork, task1, task2, join, afterJoin, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("s0", start, fork),
                new SequenceFlow("s1", fork, task1),
                new SequenceFlow("s2", fork, task2),
                new SequenceFlow("s3", task1, join),
                new SequenceFlow("s4", task2, join),
                new SequenceFlow("s5", join, afterJoin),
                new SequenceFlow("s6", afterJoin, end)
            }
        };
    }
}
