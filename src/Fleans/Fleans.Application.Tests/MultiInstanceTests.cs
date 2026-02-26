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
    public async Task MultiInstance_Cardinality_ShouldExecuteNTimes()
    {
        // Arrange — workflow: start → task (multi-instance x3) → end
        var start = new StartEvent("start");
        var task = new TaskActivity("task")
        {
            LoopCharacteristics = new MultiInstanceLoopCharacteristics(
                IsSequential: false,
                LoopCardinality: 3,
                InputCollection: null,
                InputDataItem: null,
                OutputCollection: null,
                OutputDataItem: null)
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "miCardinalityTest",
            Activities = [start, task, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, task),
                new SequenceFlow("s2", task, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("miCardinalityTest");
        var instanceId = instance.GetPrimaryKey();

        // Act — start workflow (iterations suspend at TaskActivity)
        await instance.StartWorkflow();

        // Verify suspended state: host + 3 iterations are active
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(midSnapshot);
        Assert.IsFalse(midSnapshot.IsCompleted, "Workflow should not be completed yet");
        var activeTaskCount = midSnapshot.ActiveActivities.Count(a => a.ActivityId == "task");
        Assert.AreEqual(4, activeTaskCount, "Should have 4 active task entries (1 host + 3 iterations)");

        // Complete each iteration (GetFirstActivePreferIteration picks iterations before host)
        await instance.CompleteActivity("task", new ExpandoObject());
        await instance.CompleteActivity("task", new ExpandoObject());
        await instance.CompleteActivity("task", new ExpandoObject());

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No active activities should remain");

        // Task entries: 1 host + 3 iterations = 4 completed entries with activityId "task"
        var taskCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "task");
        Assert.AreEqual(4, taskCompletions, "Should have 4 completed task entries (1 host + 3 iterations)");
    }

    [TestMethod]
    public async Task MultiInstance_Collection_ShouldIterateOverItems()
    {
        // Arrange — workflow: start → setItems → script (multi-instance over items) → end
        var start = new StartEvent("start");
        var setItems = new ScriptTask("setItems",
            "_context.items = new System.Collections.Generic.List<object> { \"A\", \"B\", \"C\" }");
        var script = new ScriptTask("script", "_context.result = \"processed-\" + _context.item")
        {
            LoopCharacteristics = new MultiInstanceLoopCharacteristics(
                IsSequential: false,
                LoopCardinality: null,
                InputCollection: "items",
                InputDataItem: "item",
                OutputCollection: "results",
                OutputDataItem: "result")
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "miCollectionTest",
            Activities = [start, setItems, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, setItems),
                new SequenceFlow("s2", setItems, script),
                new SequenceFlow("s3", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("miCollectionTest");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — ScriptTask completes via async stream, so poll for completion
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Verify output aggregation — results should exist in variables
        var rootVars = snapshot.VariableStates
            .SelectMany(vs => vs.Variables)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        Assert.IsTrue(rootVars.ContainsKey("results"), "Output collection 'results' should exist");
    }

    [TestMethod]
    public async Task MultiInstance_ShouldCleanupChildVariableScopes()
    {
        // Arrange
        var start = new StartEvent("start");
        var script = new ScriptTask("script", "_context.iterResult = \"val\"")
        {
            LoopCharacteristics = new MultiInstanceLoopCharacteristics(
                IsSequential: false,
                LoopCardinality: 3,
                InputCollection: null,
                InputDataItem: null,
                OutputCollection: null,
                OutputDataItem: null)
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "miCleanupTest",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("miCleanupTest");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — ScriptTask completes via async stream, so poll for completion
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Child variable scopes should be cleaned up — only root scope should remain
        Assert.AreEqual(1, snapshot.VariableStates.Count,
            "Only root variable scope should remain after multi-instance cleanup");

        // iterResult should NOT leak to parent scope
        var rootVars = snapshot.VariableStates[0].Variables;
        Assert.IsFalse(rootVars.ContainsKey("iterResult"),
            "Child iteration variables should not leak to parent scope");
    }

    [TestMethod]
    public async Task MultiInstance_IterationFails_ShouldFailHostAndTriggerBoundary()
    {
        // Arrange — workflow: start → task (multi-instance x3) → happyEnd
        //   with errorBoundary on task → errorHandler → end2
        var start = new StartEvent("start");
        var task = new TaskActivity("task")
        {
            LoopCharacteristics = new MultiInstanceLoopCharacteristics(
                IsSequential: false,
                LoopCardinality: 3,
                InputCollection: null,
                InputDataItem: null,
                OutputCollection: null,
                OutputDataItem: null)
        };
        var happyEnd = new EndEvent("happyEnd");
        var errorBoundary = new BoundaryErrorEvent("errorBoundary", "task", null);
        var errorHandler = new TaskActivity("errorHandler");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "miFailTest",
            Activities = [start, task, happyEnd, errorBoundary, errorHandler, end2],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, task),
                new SequenceFlow("s2", task, happyEnd),
                new SequenceFlow("s3", errorBoundary, errorHandler),
                new SequenceFlow("s4", errorHandler, end2)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("miFailTest");
        var instanceId = instance.GetPrimaryKey();

        // Act — start workflow, then fail one iteration
        await instance.StartWorkflow();
        await instance.FailActivity("task", new Exception("Iteration failed"));

        // Assert — error boundary should have fired
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(finalSnapshot);

        Assert.IsTrue(
            finalSnapshot.ActiveActivities.Any(a => a.ActivityId == "errorHandler"),
            "Error handler should be active after multi-instance iteration failure");

        Assert.IsTrue(
            finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "errorBoundary"),
            "Error boundary event should be completed");
    }
}
