using Fleans.Application.Grains;
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
        // Arrange — workflow: start → script (MI x3 with output aggregation) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter"),
            IsSequential: false,
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "result");
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

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-cardinality-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
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

        // Verify output aggregation
        var rootVars = snapshot.VariableStates.FirstOrDefault();
        Assert.IsNotNull(rootVars, "Root variable state should exist");
        Assert.IsTrue(rootVars.Variables.ContainsKey("results"), "Output collection 'results' should be present");
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

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-collection-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
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
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "result");
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

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-sequential-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
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
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "iterResult");
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

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-cleanup-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
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

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-empty-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
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
    public async Task ParallelCardinality_WhenIterationFails_ShouldFailHostAndCancelSiblings()
    {
        // Arrange — workflow: start → script (MI x3, all iterations fail via FAIL marker) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "FAIL"),
            IsSequential: false,
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "result");
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

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-fail-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — poll until no active activities remain (host fails, siblings cancelled)
        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);

        // The failed iteration should have an error state
        var failedIterations = snapshot.CompletedActivities
            .Where(a => a.ActivityId == "script" && a.ErrorState is not null)
            .ToList();
        Assert.IsTrue(failedIterations.Count >= 1, $"At least one script iteration should have failed, got {failedIterations.Count}");

        var errorState = failedIterations.First().ErrorState!;
        Assert.AreEqual("500", errorState.Code, "Generic exception should produce error code 500");

        // The MI host should also be completed (failed) — host + iterations = 4 total
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "All 3 iterations + host should be in completed list");

        // Siblings should be cancelled
        var cancelledSiblings = snapshot.CompletedActivities
            .Where(a => a.ActivityId == "script" && a.IsCancelled)
            .ToList();
        Assert.IsTrue(cancelledSiblings.Count >= 1, $"At least one sibling should be cancelled, got {cancelledSiblings.Count}");
    }

    [TestMethod]
    public async Task SequentialCollection_ShouldIterateAndAggregateOutputInOrder()
    {
        // Arrange — workflow: start → script (sequential MI over items) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"processed-\" + _context.item"),
            IsSequential: true,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-seq-collection-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-seq-collection-test");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        dynamic initVars = new ExpandoObject();
        initVars.items = new List<object> { "X", "Y", "Z" };
        await instance.SetInitialVariables(initVars);

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "Script should have completed 4 times (3 iterations + 1 host)");

        var rootVars = snapshot.VariableStates.FirstOrDefault();
        Assert.IsNotNull(rootVars, "Root variable state should exist");
        Assert.IsTrue(rootVars.Variables.ContainsKey("results"), "Output collection 'results' should be present");
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

    [TestMethod]
    public async Task ParallelCollection_OnSubProcess_ShouldCompleteEachIterationAndHost()
    {
        // Regression: an iteration entry of a multi-instance SubProcess shares the host's
        // ActivityId, so scope-completion looked up the MultiInstanceActivity wrapper instead
        // of the inner SubProcess and never completed the iteration — the host hung forever.
        var subStart = new StartEvent("subStart");
        var body = new ScriptTask("body", "_context.result = \"sub-\" + _context.item");
        var subEnd = new EndEvent("subEnd");
        var subProcess = new SubProcess("perItem")
        {
            Activities = [subStart, body, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, body),
                new SequenceFlow("sf2", body, subEnd)
            ]
        };
        var start = new StartEvent("start");
        var mi = new MultiInstanceActivity(
            "perItem",
            subProcess,
            IsSequential: false,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-subprocess-test",
            Activities = [start, mi, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, mi),
                new SequenceFlow("s2", mi, end)
            ]
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-subprocess-test");
        await processGrain.DeployVersion(workflow, "<xml/>");
        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        dynamic vars = new ExpandoObject();
        vars.items = new List<object> { "A", "B", "C" };
        await instance.SetInitialVariables((ExpandoObject)vars);
        await instance.StartWorkflow();

        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted,
            $"Workflow should complete. Active: [{string.Join(",", snapshot.ActiveActivityIds)}]");
        Assert.AreEqual(3, snapshot.CompletedActivities.Count(a => a.ActivityId == "body"));
        Assert.AreEqual(4, snapshot.CompletedActivities.Count(a => a.ActivityId == "perItem"),
            "3 iterations + 1 host should be completed");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");

        // The test cluster's SimpleScriptExecutor does not evaluate scripts, so only the
        // presence of the aggregated collection is asserted here; values are covered by
        // the E2E spec MultiInstanceScopeTests.
        Assert.IsTrue(
            snapshot.VariableStates.Any(v => v.Variables.ContainsKey("results")),
            "Output collection 'results' should be present");
    }

    [TestMethod]
    [Ignore("Blocked by #782: child completion is routed by ActivityId, which MI iterations share.")]
    public async Task ParallelCardinality_OnCallActivity_ShouldSpawnChildPerIterationAndComplete()
    {
        var childStart = new StartEvent("childStart");
        var childScript = new ScriptTask("childScript", "_context.result = \"child-\" + _context.loopCounter");
        var childEnd = new EndEvent("childEnd");
        var child = new WorkflowDefinition
        {
            WorkflowId = "mi-call-child",
            Activities = [childStart, childScript, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("c1", childStart, childScript),
                new SequenceFlow("c2", childScript, childEnd)
            ]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-call-child")
            .DeployVersion(child, "<xml/>");

        var start = new StartEvent("start");
        var mi = new MultiInstanceActivity(
            "callChild",
            new CallActivity("callChild", "mi-call-child", [], []),
            IsSequential: false,
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");
        var parent = new WorkflowDefinition
        {
            WorkflowId = "mi-call-parent",
            Activities = [start, mi, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, mi),
                new SequenceFlow("s2", mi, end)
            ]
        };
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-call-parent");
        await processGrain.DeployVersion(parent, "<xml/>");
        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        await instance.StartWorkflow();

        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted,
            $"Workflow should complete. Active: [{string.Join(",", snapshot.ActiveActivityIds)}], " +
            $"children: [{string.Join(",", snapshot.ActiveActivities.Select(a => a.ChildWorkflowInstanceId))}]");
        Assert.AreEqual(4, snapshot.CompletedActivities.Count(a => a.ActivityId == "callChild"),
            "3 iterations + 1 host should be completed");
    }
}
