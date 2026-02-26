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
}
