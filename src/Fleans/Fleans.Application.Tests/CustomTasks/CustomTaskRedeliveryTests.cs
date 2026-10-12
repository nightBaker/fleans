using Fleans.Application.Grains;
using Fleans.Application.Tests.CustomTasks.TestStubs;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests.CustomTasks;

/// <summary>
/// Many worker silos may consume the custom-task streams in parallel and the stream is
/// at-least-once, so the same <see cref="ExecuteCustomTaskEvent"/> can be delivered more
/// than once. The plugin must run at most once per activity instance.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CustomTaskRedeliveryTests : WorkflowTestBase
{
    [TestMethod]
    public async Task Redelivered_event_for_completed_activity_does_not_rerun_plugin()
    {
        // Arrange — start → ct1 (test-no-op-a plugin) → end, run to completion.
        Interlocked.Exchange(ref TestNoOpCustomTaskHandlerA.InvocationCount, 0);
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", "test-no-op-a", null, null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "custom-task-redelivery",
            Activities = [start, ct1, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ct1),
                new SequenceFlow("f2", ct1, end)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await instance.StartWorkflow();

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(1, Volatile.Read(ref TestNoOpCustomTaskHandlerA.InvocationCount));
        var ct1Entry = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");

        // Act — redeliver the same event (same activity instance) through the engine publisher.
        var publisher = Cluster.GrainFactory.GetGrain<IEventPublisher>(0);
        await publisher.Publish(new ExecuteCustomTaskEvent(
            WorkflowInstanceId: instance.GetPrimaryKey(),
            WorkflowId: workflow.WorkflowId,
            ProcessDefinitionId: null,
            ActivityInstanceId: ct1Entry.ActivityInstanceId,
            ActivityId: "ct1",
            TaskType: "test-no-op-a",
            InputMappings: new List<InputMapping>(),
            OutputMappings: new List<OutputMapping>(),
            VariablesId: ct1Entry.VariablesStateId));
        await Task.Delay(1000);

        // Assert — the duplicate was skipped; the plugin ran exactly once.
        Assert.AreEqual(1, Volatile.Read(ref TestNoOpCustomTaskHandlerA.InvocationCount),
            "A redelivered event for an already-completed activity must not re-execute the plugin.");
        var after = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(after);
        Assert.IsTrue(after.IsCompleted);
        Assert.IsFalse(after.IsFailed);
    }
}
