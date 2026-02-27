using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MultiInstanceTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ParallelCardinality_ShouldExecuteNTimes()
    {
        // Arrange — workflow: start → script (MI x3) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter"),
            IsSequential: false,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-cardinality-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-cardinality-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — poll for completion (script execution is async via stream events)
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No active activities should remain");

        // Script should appear 3 times in completed (iterations) + 1 time for host = 4
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "Script should have completed 4 times (3 iterations + 1 host)");
    }

    [TestMethod]
    public async Task ParallelCollection_ShouldIterateOverItemsAndAggregateOutput()
    {
        // Arrange — workflow: start → script (MI over items) → end
        // Set items via SetInitialVariables (scripts execute async and may not run in test cluster)
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"processed-\" + _context.item"),
            IsSequential: false,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-collection-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-collection-test");
        var instanceId = instance.GetPrimaryKey();

        // Set initial variables with a list of items
        dynamic initVars = new ExpandoObject();
        initVars.items = new List<object> { "A", "B", "C" };
        await instance.SetInitialVariables(initVars);

        // Act
        await instance.StartWorkflow();

        // Assert — use PollForCompletion
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Verify 3 script iterations completed + 1 host = 4
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "Script should have completed 4 times (3 iterations + 1 host)");

        // Verify output aggregation — VariableStateSnapshot.Variables is Dictionary<string, string>
        var rootVars = snapshot.VariableStates.FirstOrDefault();
        Assert.IsNotNull(rootVars, "Root variable state should exist");
        Assert.IsTrue(rootVars.Variables.ContainsKey("results"), "Output collection 'results' should be present");
    }

    [TestMethod]
    public async Task SequentialCardinality_ShouldExecuteOneAtATime()
    {
        // Arrange — workflow: start → script (sequential MI x3) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter"),
            IsSequential: true,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-sequential-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-sequential-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — use PollForCompletion
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "Script should have completed 4 times (3 iterations + 1 host)");
    }

    [TestMethod]
    public async Task ParallelCardinality_ShouldCleanupChildVariableScopes()
    {
        // Arrange
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.iterResult = \"val\""),
            IsSequential: false,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-cleanup-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-cleanup-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — use PollForCompletion
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        // Child variable scopes should be cleaned up — only root scope should remain
        Assert.AreEqual(1, snapshot.VariableStates.Count,
            "Only root variable scope should remain after multi-instance cleanup");
    }

    [TestMethod]
    public async Task ParallelEmptyCollection_ShouldCompleteImmediately()
    {
        // Arrange — workflow: start → script (MI over empty list) → end
        // Set items via SetInitialVariables (scripts execute async and may not run in test cluster)
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"x\""),
            IsSequential: false,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-empty-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-empty-test");
        var instanceId = instance.GetPrimaryKey();

        // Set initial variables with an empty list
        dynamic initVars = new ExpandoObject();
        initVars.items = new List<object>();
        await instance.SetInitialVariables(initVars);

        // Act
        await instance.StartWorkflow();

        // Assert — use PollForCompletion
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete even with empty collection");
    }

    [TestMethod]
    public async Task ParallelCardinality_WhenIterationFails_ShouldSetErrorState()
    {
        // Arrange — workflow: start → script (MI x3, one throws) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "if (_context.loopCounter == 1) throw new System.Exception(\"iteration failed\"); _context.result = \"ok\""),
            IsSequential: false,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-fail-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-fail-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — poll for activity completion (some iterations succeed, one fails)
        var snapshot = await PollForActivityCompletion(instanceId, "script", expectedCount: 3);
        Assert.IsNotNull(snapshot);

        // At least one completed activity should have an error state
        var failedActivities = snapshot.CompletedActivities
            .Where(a => a.ActivityId == "script" && a.ErrorState is not null)
            .ToList();
        Assert.IsTrue(failedActivities.Count >= 1, $"At least one script iteration should have failed, got {failedActivities.Count}");

        var errorState = failedActivities.First().ErrorState!;
        Assert.AreEqual(500, errorState.Code, "Generic exception should produce error code 500");
    }

    private async Task<Application.QueryModels.InstanceStateSnapshot?> PollForCompletion(
        Guid instanceId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            if (snapshot is not null && snapshot.IsCompleted)
                return snapshot;
            await Task.Delay(100);
        }
        return await QueryService.GetStateSnapshot(instanceId);
    }

    private async Task<Application.QueryModels.InstanceStateSnapshot?> PollForActivityCompletion(
        Guid instanceId, string activityId, int expectedCount, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            if (snapshot is not null)
            {
                var completedCount = snapshot.CompletedActivities.Count(a => a.ActivityId == activityId);
                if (completedCount >= expectedCount || snapshot.IsCompleted)
                    return snapshot;
            }
            await Task.Delay(100);
        }
        return await QueryService.GetStateSnapshot(instanceId);
    }
}
