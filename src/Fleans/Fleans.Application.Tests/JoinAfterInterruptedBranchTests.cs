using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// A branch of a fork is interrupted by a boundary whose path rejoins the same join: the boundary path carries the
/// branch's token, so the join fires exactly once, after the other branch arrives too.
/// </summary>
[TestClass]
public class JoinAfterInterruptedBranchTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ParallelJoin_BranchInterruptedByATimer_RejoinsThroughTheBoundaryPath_AndTheJoinFiresOnce()
        => await RunInterruptedBranch(
            "parallel-join-interrupted-branch",
            new ParallelGateway("fork", IsFork: true),
            new ParallelGateway("join", IsFork: false),
            (fork, target, id) => new SequenceFlow(id, fork, target));

    [TestMethod]
    public async Task InclusiveJoin_BranchInterruptedByATimer_RejoinsThroughTheBoundaryPath_AndTheJoinFiresOnce()
        => await RunInterruptedBranch(
            "inclusive-join-interrupted-branch",
            new InclusiveGateway("fork", IsFork: true),
            new InclusiveGateway("join", IsFork: false),
            (fork, target, id) => new ConditionalSequenceFlow(id, fork, target, "true"));

    /// <summary>start → fork → (a [timer → a_timed_out], b) → join → end.</summary>
    private async Task RunInterruptedBranch(string workflowId, Activity fork, Activity join,
        Func<Activity, Activity, string, SequenceFlow> forkFlow)
    {
        // Arrange
        var start = new StartEvent("start");
        var a = new TaskActivity("a");
        var b = new TaskActivity("b");
        var timeout = new BoundaryTimerEvent("a_timeout", "a", new TimerDefinition(TimerType.Duration, "PT30M"));
        var timedOut = new TaskActivity("a_timed_out");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, fork, a, b, timeout, timedOut, join, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                forkFlow(fork, a, "toA"),
                forkFlow(fork, b, "toB"),
                new SequenceFlow("aJoin", a, join),
                new SequenceFlow("bJoin", b, join),
                new SequenceFlow("timeoutFlow", timeout, timedOut),
                new SequenceFlow("timedOutJoin", timedOut, join),
                new SequenceFlow("f2", join, end)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("a") && s.ActiveActivityIds.Contains("b"));
        var aInstance = running.ActiveActivities.Single(x => x.ActivityId == "a").ActivityInstanceId;

        // Act — the timer interrupts a; its path reaches the join first
        await instance.HandleTimerFired("a_timeout", aInstance);
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("a_timed_out"));
        await instance.CompleteActivity("a_timed_out", new ExpandoObject());
        var waiting = await WaitForCondition(instanceId, s => s.CompletedActivityIds.Contains("a_timed_out"));
        CollectionAssert.DoesNotContain(waiting.CompletedActivityIds, "end", "the join waits for b");

        await instance.CompleteActivity("b", new ExpandoObject());

        // Assert
        var done = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(done.CompletedActivities.Single(x => x.ActivityId == "a").IsCancelled);
        Assert.AreEqual(1, done.CompletedActivityIds.Count(id => id == "end"), "the join fires once");
        Assert.IsFalse(done.IsFailed);
    }
}
