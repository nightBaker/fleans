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
/// than once. A delivery that arrives after the activity finished must not re-run the plugin.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CustomTaskRedeliveryTests : WorkflowTestBase
{
    // CustomTaskHandlerBase.LogSkippedInactiveActivity — the positive signal that a duplicate was handled.
    private const int SkippedInactiveActivityEventId = 4038;

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
        await instance.StartWorkflow(workflow);

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(1, Volatile.Read(ref TestNoOpCustomTaskHandlerA.InvocationCount));
        var ct1Entry = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");

        // Act — redeliver the same event (same activity instance) through the engine publisher.
        CapturedLogEvents.Clear();
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
            VariablesId: ct1Entry.VariablesStateId,
            BusinessId: null));
        await CapturedLogEvents.WaitFor<TestNoOpCustomTaskHandlerA>(SkippedInactiveActivityEventId, 1);

        // Assert — the duplicate was skipped; the plugin ran exactly once.
        Assert.AreEqual(1, Volatile.Read(ref TestNoOpCustomTaskHandlerA.InvocationCount),
            "A redelivered event for an already-completed activity must not re-execute the plugin.");
        var after = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(after);
        Assert.IsTrue(after.IsCompleted);
        Assert.IsFalse(after.IsFailed);
    }

    [TestMethod]
    public async Task Many_silos_run_distinct_instances_in_parallel_and_skip_queued_duplicates()
    {
        // Arrange — three worker silos; N instances each with one gated custom task.
        // Scope: parallelism across instances + duplicates queued behind an in-flight run on the
        // same activation. A race between two activations of one handler (possible briefly during
        // membership changes) is not reproducible here and is not covered — see invariants.md guard 8.
        TestGatedCustomTaskHandler.Reset();
        CapturedLogEvents.Clear();
        await Cluster.StartAdditionalSiloAsync();
        await WaitForActiveSilos(3);
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
            await i.StartWorkflow(workflow);
        }));

        try
        {
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
                    VariablesId: entry.VariablesStateId,
            BusinessId: null));
            }

            // Release the originals; the queued duplicates arrive after completion.
            TestGatedCustomTaskHandler.Gate.TrySetResult();
            foreach (var instance in instances)
            {
                var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
                Assert.IsFalse(done.IsFailed);
            }
            await CapturedLogEvents.WaitFor<TestGatedCustomTaskHandler>(SkippedInactiveActivityEventId, instanceCount);
        }
        finally
        {
            // Never leave gated handlers hanging if an assertion above fails.
            TestGatedCustomTaskHandler.Gate.TrySetResult();
        }

        // Assert — exactly one execution per task, spread over more than one worker silo.
        var executions = TestGatedCustomTaskHandler.Executions.ToList();
        Assert.AreEqual(instanceCount, executions.Count, "Each task must execute exactly once.");
        Assert.AreEqual(instanceCount, executions.Select(e => e.ActivityInstanceId).Distinct().Count(),
            "No task may be executed by two workers.");
        Assert.IsTrue(executions.Select(e => e.SiloName).Distinct().Count() > 1,
            "Tasks must be distributed across more than one worker silo.");
    }

    [TestMethod]
    public async Task Redelivered_event_for_failed_activity_does_not_rerun_plugin()
    {
        // Arrange — start → ct1 (plugin always throws) → end; the activity fails.
        Interlocked.Exchange(ref TestThrowingCustomTaskHandler.InvocationCount, 0);
        var workflow = SingleCustomTaskWorkflow("custom-task-redelivery-failed", "test-throwing");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        Assert.AreEqual(1, Volatile.Read(ref TestThrowingCustomTaskHandler.InvocationCount));
        var ct1Entry = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");

        // Act — redeliver the same event after the failure.
        CapturedLogEvents.Clear();
        await PublishCustomTaskEvent(instance.GetPrimaryKey(), workflow.WorkflowId, "test-throwing",
            ct1Entry.ActivityInstanceId, ct1Entry.VariablesStateId);
        await CapturedLogEvents.WaitFor<TestThrowingCustomTaskHandler>(SkippedInactiveActivityEventId, 1);

        // Assert — skipped: the plugin did not run again and the instance stays failed.
        Assert.AreEqual(1, Volatile.Read(ref TestThrowingCustomTaskHandler.InvocationCount),
            "A redelivered event for an already-failed activity must not re-execute the plugin.");
        var after = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(after);
        Assert.IsTrue(after.IsFailed);
    }

    [TestMethod]
    public async Task Deterministic_activity_check_error_fails_the_activity_without_running_plugin()
    {
        // Arrange — ct1 uses a task type nobody handles, so it stays Active.
        Interlocked.Exchange(ref TestNoOpCustomTaskHandlerA.InvocationCount, 0);
        var workflow = SingleCustomTaskWorkflow("custom-task-bad-variables", "unhandled-type");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "ct1"));
        var ct1Entry = snapshot.ActiveActivities.Single(a => a.ActivityId == "ct1");

        // Act — an event for that active activity whose variable scope does not exist: the
        // activity check throws a deterministic error that redelivery could never fix.
        await PublishCustomTaskEvent(instance.GetPrimaryKey(), workflow.WorkflowId, "test-no-op-a",
            ct1Entry.ActivityInstanceId, variablesId: Guid.NewGuid());

        // Assert — the activity fails visibly instead of staying Active; the plugin never ran.
        var after = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        Assert.IsFalse(after.ActiveActivities.Any(a => a.ActivityId == "ct1"));
        Assert.AreEqual(0, Volatile.Read(ref TestNoOpCustomTaskHandlerA.InvocationCount));
    }

    [TestMethod]
    public async Task Handler_activation_is_freed_once_the_task_completed()
    {
        // Arrange — the gate holds the plugin, so the handler is observably active first.
        TestGatedCustomTaskHandler.Reset();
        var workflow = SingleCustomTaskWorkflow("custom-task-handler-freed-completed", "test-gated");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (TestGatedCustomTaskHandler.Executions.IsEmpty)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "the gated plugin never ran");
            await Task.Delay(50);
        }
        Assert.IsTrue(await IsHandlerActive(typeof(TestGatedCustomTaskHandler), instance.GetPrimaryKey()));

        // Act
        TestGatedCustomTaskHandler.Gate.TrySetResult();

        // Assert — the instance completed and its handler grain did not wait for idle collection
        // (15 min): one activation per instance is what filled a plugin host's memory (#922).
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        await WaitForNoHandlerActivation(typeof(TestGatedCustomTaskHandler), instance.GetPrimaryKey());
    }

    [TestMethod]
    public async Task Handler_activation_is_freed_once_the_task_failed()
    {
        // Arrange
        var workflow = SingleCustomTaskWorkflow("custom-task-handler-freed-failed", "test-throwing");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "ct1"));
        await WaitForNoHandlerActivation(typeof(TestThrowingCustomTaskHandler), instance.GetPrimaryKey());
    }

    private async Task<bool> IsHandlerActive(Type handlerType, Guid workflowInstanceId)
    {
        // Handler grains are keyed by the instance id ("D"); the grain type is the lower-cased class name.
        var prefix = $"{handlerType.Name.ToLowerInvariant()}/";
        var key = workflowInstanceId.ToString("D");
        var stats = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        return stats.Any(s => s.GrainId.ToString() is var id && id.StartsWith(prefix) && id.Contains(key));
    }

    private async Task WaitForNoHandlerActivation(Type handlerType, Guid workflowInstanceId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await IsHandlerActive(handlerType, workflowInstanceId))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline,
                $"{handlerType.Name} for instance {workflowInstanceId} is still active after the task finished.");
            await Task.Delay(100);
        }
    }

    private static WorkflowDefinition SingleCustomTaskWorkflow(string workflowId, string taskType)
    {
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", taskType, null, null);
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, ct1, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ct1),
                new SequenceFlow("f2", ct1, end)
            ]
        };
    }

    private Task PublishCustomTaskEvent(
        Guid workflowInstanceId, string workflowId, string taskType, Guid activityInstanceId, Guid variablesId)
        => Cluster.GrainFactory.GetGrain<IEventPublisher>(0).Publish(new ExecuteCustomTaskEvent(
            WorkflowInstanceId: workflowInstanceId,
            WorkflowId: workflowId,
            ProcessDefinitionId: null,
            ActivityInstanceId: activityInstanceId,
            ActivityId: "ct1",
            TaskType: taskType,
            InputMappings: new List<InputMapping>(),
            OutputMappings: new List<OutputMapping>(),
            VariablesId: variablesId,
            BusinessId: null));

    private async Task WaitForActiveSilos(int count)
    {
        var management = Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await management.GetHosts(onlyActive: true)).Count < count)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Cluster did not reach {count} active silos.");
            await Task.Delay(100);
        }
    }
}
