using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// An interrupting multiple boundary event (message + signal + timer) on a task: whichever definition fires first
/// interrupts the task and takes the boundary path once, and the other definitions are released, so a later
/// message or signal reaches nobody.
/// </summary>
[TestClass]
public class MultipleBoundaryEventTests : WorkflowTestBase
{
    private const string MessageName = "multiBoundaryCancel";

    [TestMethod]
    public async Task MessageFiresFirst_InterruptsTheTask_AndReleasesTheSignalAndTimer()
    {
        var run = await Start();

        Assert.IsTrue(await DeliverMessageWhenSubscribed(MessageName, run.OrderId));

        await AssertBoundaryTakenOnce(run, messageReleased: true);
    }

    [TestMethod]
    public async Task SignalFiresFirst_InterruptsTheTask_AndReleasesTheMessageAndTimer()
    {
        var run = await Start();

        Assert.AreEqual(1, await BroadcastSignalWhenSubscribed(run.SignalName));

        await AssertBoundaryTakenOnce(run, messageReleased: false);
    }

    [TestMethod]
    public async Task TimerFiresFirst_InterruptsTheTask_AndReleasesTheMessageAndSignal()
    {
        var run = await Start();

        await run.Instance.HandleTimerFired("anyEvent", run.TaskInstanceId);

        await AssertBoundaryTakenOnce(run, messageReleased: false);
    }

    private sealed record Run(IWorkflowInstanceGrain Instance, Guid InstanceId, Guid TaskInstanceId, string OrderId, string SignalName);

    /// <summary>start → task1 → end; interrupting multiple boundary anyEvent (message, signal, 30 min timer) on task1 → recovery → recovery_end.</summary>
    private async Task<Run> Start()
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var signalName = $"multiBoundaryAbort-{Guid.NewGuid():N}";
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        var boundary = new MultipleBoundaryEvent("anyEvent", "task1",
        [
            new MessageEventDef("msg1"),
            new SignalEventDef("sig1"),
            new TimerEventDef(new TimerDefinition(TimerType.Duration, "PT30M"))
        ]);
        var recovery = new TaskActivity("recovery");
        var recoveryEnd = new EndEvent("recovery_end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "multiple-boundary",
            Activities = [start, task, end, boundary, recovery, recoveryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, recovery),
                new SequenceFlow("f4", recovery, recoveryEnd)
            ],
            Messages = [new MessageDefinition("msg1", MessageName, "orderId")],
            Signals = [new SignalDefinition("sig1", signalName)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        dynamic vars = new ExpandoObject();
        vars.orderId = orderId;
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);
        var running = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("task1"));
        var taskInstanceId = running.ActiveActivities.Single(a => a.ActivityId == "task1").ActivityInstanceId;
        return new Run(instance, instance.GetPrimaryKey(), taskInstanceId, orderId, signalName);
    }

    private async Task AssertBoundaryTakenOnce(Run run, bool messageReleased)
    {
        var recovering = await WaitForCondition(run.InstanceId, s => s.ActiveActivityIds.Contains("recovery"));
        Assert.IsTrue(recovering.CompletedActivities.Single(a => a.ActivityId == "task1").IsCancelled, "the task is interrupted");

        // The definitions that did not fire are released: a late message or signal reaches nobody.
        if (!messageReleased)
        {
            var late = await Cluster.GrainFactory
                .GetGrain<IMessageCorrelationGrain>(MessageCorrelationKey.Build(MessageName, run.OrderId))
                .DeliverMessage(new ExpandoObject());
            Assert.IsFalse(late, "the message subscription is released");
        }
        Assert.AreEqual(0, await BroadcastSignal(run.SignalName), "the signal subscription is released");

        await run.Instance.CompleteActivity("recovery", new ExpandoObject());
        var done = await WaitForCondition(run.InstanceId, s => s.IsCompleted);
        Assert.AreEqual(1, done.CompletedActivityIds.Count(id => id == "anyEvent"), "the boundary fires once");
        Assert.AreEqual(1, done.CompletedActivityIds.Count(id => id == "recovery_end"));
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "end");
    }
}
