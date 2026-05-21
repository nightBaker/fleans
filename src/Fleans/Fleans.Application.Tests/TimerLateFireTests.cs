using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

/// <summary>
/// #658 — verifies that a late timer fire (post-completion) at
/// <see cref="WorkflowInstance.HandleTimerFired"/> is silently dropped after a
/// warning log, mirroring the aggregate's internal stale guards via
/// <c>WorkflowExecution.IsTimerFireStale</c>.
///
/// Grain-level integration: covers the regular intermediate-catch timer late-fire
/// path (host entry completed after first fire). The ESP timer matrix (root-scope
/// and SubProcess-scoped, active/completed) is covered by domain unit tests in
/// <c>IsTimerFireStaleTests</c>.
/// </summary>
[TestClass]
public class TimerLateFireTests : WorkflowTestBase
{
    [TestMethod]
    public async Task RegularIntermediateTimer_LateFire_AfterCompletion_IsSilentlyDropped()
    {
        // Arrange: Start → Timer(PT5M) → End — same shape as
        // TimerIntermediateCatch_HandleTimerFired_ShouldCompleteWorkflow.
        var start = new StartEvent("start");
        var timer = new TimerIntermediateCatchEvent("timer1",
            new TimerDefinition(TimerType.Duration, "PT5M"));
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-late-fire-regular",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end),
            ],
        };
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnap = await QueryService.GetStateSnapshot(instanceId);
        var timerInstanceId = preSnap!.ActiveActivities.First(a => a.ActivityId == "timer1").ActivityInstanceId;

        // Fire normally — workflow advances past timer1 and completes.
        await workflowInstance.HandleTimerFired("timer1", timerInstanceId);
        var midSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnap!.IsCompleted, "first HandleTimerFired should complete the workflow");
        var completedAfterFirstFire = midSnap.CompletedActivities.Count;

        // Act — fire again post-completion. The host entry for timer1 is now
        // completed; IsTimerFireStale must catch this and short-circuit.
        var lateResult = await workflowInstance.HandleTimerFired("timer1", timerInstanceId);

        // Assert: returns null (no cycle re-registration) and workflow state
        // is unchanged.
        Assert.IsNull(lateResult, "late fire must return null (no cycle re-registration)");
        var afterSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(afterSnap!.IsCompleted, "workflow should still be completed");
        Assert.AreEqual(completedAfterFirstFire, afterSnap.CompletedActivities.Count,
            "late timer fire must not advance the workflow a second time");
    }

}
