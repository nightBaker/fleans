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
        // Arrange — workflow: start → scriptTask (multi-instance x3) → end
        var start = new StartEvent("start");
        var script = new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter")
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
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("miCardinalityTest");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No active activities should remain");

        // Script task should appear 3 times in completed activities (one per iteration)
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(3, scriptCompletions, "Script task should have completed 3 times");
    }
}
