using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;

namespace Fleans.Application.Tests;

/// <summary>
/// #860: an instance activation takes its process definition from the silo's
/// <see cref="WorkflowDefinitionCache"/>, and a completed instance does not stay resident.
/// </summary>
[TestClass]
public class DefinitionCacheActivationTests : WorkflowTestBase
{
    private const int DefinitionLoadedEventId = 6050;

    [TestMethod]
    [DoNotParallelize] // CapturedLogEvents.Capture is process-wide
    public async Task Reactivation_LoadsTheDefinitionOncePerSilo_NotOncePerInstance()
    {
        // Arrange — several running instances of one deployed version, then all deactivated.
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cache-reactivation");
        var summary = await processGrain.DeployVersion(CreateWaitingWorkflow("cache-reactivation"), "<bpmn/>");
        var instances = new List<IWorkflowInstanceGrain>();
        for (var i = 0; i < 6; i++)
        {
            var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await instance.StartWorkflow((await processGrain.GetStartableDefinition())!);
            instances.Add(instance);
        }
        var ids = new List<Guid>();
        foreach (var instance in instances)
            ids.Add(await instance.GetWorkflowInstanceId());
        await ForceAllGrainDeactivation([.. ids]);
        foreach (var id in ids)
            await WaitUntilInactive(id);

        using var capture = CapturedLogEvents.Capture(DefinitionLoadedEventId);

        // Act — every reactivated instance needs its definition to complete the task.
        foreach (var instance in instances)
            await instance.CompleteActivity("task", new ExpandoObject());

        // Assert
        foreach (var id in ids)
        {
            var snapshot = await QueryService.GetStateSnapshot(id);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.IsCompleted, $"instance {id} should complete after reactivation");
        }

        var loads = CapturedLogEvents.Entries(DefinitionLoadedEventId)
            .Count(e => e.Message.Contains(summary.ProcessDefinitionId, StringComparison.Ordinal));
        Assert.IsTrue(loads >= 1, "the first activation in a silo loads the definition");
        Assert.IsTrue(loads <= Cluster.Silos.Count,
            $"one load per silo at most, not one per activation: {loads} loads for {instances.Count} instances on {Cluster.Silos.Count} silos");
    }

    [TestMethod]
    public async Task CompletedInstance_DeactivatesWithoutWaitingForCollection()
    {
        // Arrange
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cache-completed");
        await processGrain.DeployVersion(CreateWaitingWorkflow("cache-completed"), "<bpmn/>");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow((await processGrain.GetStartableDefinition())!);
        var id = await instance.GetWorkflowInstanceId();

        // Act
        await instance.CompleteActivity("task", new ExpandoObject());

        // Assert — gone from the silo without ForceActivationCollection, state still readable.
        await WaitUntilInactive(id);

        var snapshot = await instance.GetStateSnapshot();
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task RunningInstance_StaysActive()
    {
        // Arrange
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cache-running");
        await processGrain.DeployVersion(CreateWaitingWorkflow("cache-running"), "<bpmn/>");
        var definition = (await processGrain.GetStartableDefinition())!;
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var id = await instance.GetWorkflowInstanceId();

        // Act
        await instance.StartWorkflow(definition);
        await Task.Delay(500);

        // Assert
        Assert.IsTrue(await IsActive(id), "only a terminal instance deactivates early");
    }

    private async Task WaitUntilInactive(Guid instanceId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await IsActive(instanceId))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"instance {instanceId} did not deactivate");
            await Task.Delay(100);
        }
    }

    private async Task<bool> IsActive(Guid instanceId)
    {
        var key = $"workflowinstance/{instanceId:N}";
        var stats = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        return stats.Any(s => s.GrainId.ToString() == key);
    }

    private static WorkflowDefinition CreateWaitingWorkflow(string workflowId)
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end)
            ]
        };
    }
}
