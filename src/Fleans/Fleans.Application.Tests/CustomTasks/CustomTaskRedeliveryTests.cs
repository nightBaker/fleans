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

    [TestMethod]
    public async Task Many_workers_run_tasks_in_parallel_and_never_execute_the_same_task_twice()
    {
        // Arrange — three worker silos; N instances each with one gated custom task.
        TestGatedCustomTaskHandler.Reset();
        await Cluster.StartAdditionalSiloAsync();
        const int instanceCount = 30;
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", "test-gated", null, null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "custom-task-many-workers",
            Activities = [start, ct1, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ct1),
                new SequenceFlow("f2", ct1, end)
            ]
        };
        var instances = Enumerable.Range(0, instanceCount)
            .Select(_ => Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid()))
            .ToList();
        await Task.WhenAll(instances.Select(async i =>
        {
            await i.SetWorkflow(workflow);
            await i.StartWorkflow();
        }));

        // Act 1 — wait until every task is executing at the same time (held by the gate).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && TestGatedCustomTaskHandler.Executions.Count < instanceCount)
            await Task.Delay(50);
        Assert.AreEqual(instanceCount, TestGatedCustomTaskHandler.MaxInFlight,
            "All tasks must be able to run concurrently across workers.");

        // Act 2 — while every task is in flight, deliver a duplicate event for each one.
        var publisher = Cluster.GrainFactory.GetGrain<IEventPublisher>(0);
        foreach (var instance in instances)
        {
            var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
                s => s.ActiveActivities.Any(a => a.ActivityId == "ct1"));
            var entry = snapshot.ActiveActivities.Single(a => a.ActivityId == "ct1");
            await publisher.Publish(new ExecuteCustomTaskEvent(
                WorkflowInstanceId: instance.GetPrimaryKey(),
                WorkflowId: workflow.WorkflowId,
                ProcessDefinitionId: null,
                ActivityInstanceId: entry.ActivityInstanceId,
                ActivityId: "ct1",
                TaskType: "test-gated",
                InputMappings: new List<InputMapping>(),
                OutputMappings: new List<OutputMapping>(),
                VariablesId: entry.VariablesStateId));
        }

        // Release the originals; the queued duplicates arrive after completion.
        TestGatedCustomTaskHandler.Gate.TrySetResult();
        foreach (var instance in instances)
        {
            var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
            Assert.IsFalse(done.IsFailed);
        }
        await Task.Delay(1000);

        // Assert — exactly one execution per task, spread over more than one worker silo.
        var executions = TestGatedCustomTaskHandler.Executions.ToList();
        Assert.AreEqual(instanceCount, executions.Count, "Each task must execute exactly once.");
        Assert.AreEqual(instanceCount, executions.Select(e => e.ActivityInstanceId).Distinct().Count(),
            "No task may be executed by two workers.");
        Assert.IsTrue(executions.Select(e => e.SiloName).Distinct().Count() > 1,
            "Tasks must be distributed across more than one worker silo.");
    }
}
