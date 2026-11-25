using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// top → middle → bottom through two call activities, with the only boundary on top's call activity: an error and
/// an escalation raised in bottom must travel through middle, which has no boundary of its own, to top.
/// </summary>
[TestClass]
public class CallActivityThreeLevelTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ErrorInTheBottomProcess_ReachesTheErrorBoundaryOfTheTopProcess()
    {
        // Arrange
        var (top, bottomId) = await StartThreeLevels("err", new BoundaryErrorEvent("onError", "callMiddle", null), bottomThrows: null);

        // Act
        await Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(bottomId)
            .FailActivity("bottomTask", new InvalidOperationException("bottom failed"));

        // Assert
        await AssertTopTookTheBoundary(top);
    }

    [TestMethod]
    public async Task EscalationInTheBottomProcess_ReachesTheEscalationBoundaryOfTheTopProcess()
    {
        // Arrange
        var (top, bottomId) = await StartThreeLevels("esc",
            new EscalationBoundaryEvent("onEscalation", "callMiddle", "ESC-DEEP", IsInterrupting: true),
            bottomThrows: new EscalationIntermediateThrowEvent("bottomEscalate", "ESC-DEEP"));

        // Act
        await Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(bottomId)
            .CompleteActivity("bottomTask", new ExpandoObject());

        // Assert
        await AssertTopTookTheBoundary(top);
    }

    // --- helpers ---

    /// <summary>
    /// bottom: start → bottomTask → [bottomThrows →] end. middle: start → callBottom → end.
    /// top: start → callMiddle → end, with <paramref name="boundary"/> on callMiddle → recovery → recovery_end.
    /// </summary>
    private async Task<(IWorkflowInstanceGrain Top, Guid BottomId)> StartThreeLevels(string suffix, Activity boundary, Activity? bottomThrows)
    {
        var bottomId = $"three-level-bottom-{suffix}";
        var middleId = $"three-level-middle-{suffix}";
        var topId = $"three-level-top-{suffix}";

        var bStart = new StartEvent("bottomStart");
        var bTask = new TaskActivity("bottomTask");
        var bEnd = new EndEvent("bottomEnd");
        List<Activity> bActivities = [bStart, bTask, bEnd];
        List<SequenceFlow> bFlows = [new("b1", bStart, bTask)];
        if (bottomThrows is null)
        {
            bFlows.Add(new("b2", bTask, bEnd));
        }
        else
        {
            bActivities.Add(bottomThrows);
            bFlows.AddRange([new("b2", bTask, bottomThrows), new("b3", bottomThrows, bEnd)]);
        }
        await Deploy(new WorkflowDefinition { WorkflowId = bottomId, Activities = bActivities, SequenceFlows = bFlows });

        var mStart = new StartEvent("middleStart");
        var callBottom = new CallActivity("callBottom", bottomId, [], []);
        var mEnd = new EndEvent("middleEnd");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = middleId,
            Activities = [mStart, callBottom, mEnd],
            SequenceFlows = [new("m1", mStart, callBottom), new("m2", callBottom, mEnd)]
        });

        var tStart = new StartEvent("start");
        var callMiddle = new CallActivity("callMiddle", middleId, [], []);
        var tEnd = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var recoveryEnd = new EndEvent("recovery_end");
        var topDefinition = await Deploy(new WorkflowDefinition
        {
            WorkflowId = topId,
            Activities = [tStart, callMiddle, tEnd, boundary, recovery, recoveryEnd],
            SequenceFlows =
            [
                new("t1", tStart, callMiddle),
                new("t2", callMiddle, tEnd),
                new("t3", boundary, recovery),
                new("t4", recovery, recoveryEnd)
            ]
        });

        var top = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await top.StartWorkflow(topDefinition);
        var topState = await WaitForCondition(top.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "callMiddle" && a.ChildWorkflowInstanceId is not null));
        var middle = topState.ActiveActivities.Single(a => a.ActivityId == "callMiddle").ChildWorkflowInstanceId!.Value;
        var middleState = await WaitForCondition(middle,
            s => s.ActiveActivities.Any(a => a.ActivityId == "callBottom" && a.ChildWorkflowInstanceId is not null));
        var bottom = middleState.ActiveActivities.Single(a => a.ActivityId == "callBottom").ChildWorkflowInstanceId!.Value;
        await WaitForCondition(bottom, s => s.ActiveActivityIds.Contains("bottomTask"));
        return (top, bottom);
    }

    private async Task<IWorkflowDefinition> Deploy(WorkflowDefinition definition)
    {
        var process = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(definition.WorkflowId);
        await process.DeployVersion(definition, "<xml/>");
        return await process.GetLatestDefinition();
    }

    private async Task AssertTopTookTheBoundary(IWorkflowInstanceGrain top)
    {
        var recovering = await WaitForCondition(top.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("recovery"));
        var callMiddle = recovering.CompletedActivities.Single(a => a.ActivityId == "callMiddle");
        Assert.IsTrue(callMiddle.IsCancelled || callMiddle.ErrorState is not null,
            "the top call activity is closed: failed by the error, or cancelled by the escalation boundary");
        await top.CompleteActivity("recovery", new ExpandoObject());
        var done = await WaitForCondition(top.GetPrimaryKey(), s => s.IsCompleted);
        CollectionAssert.Contains(done.CompletedActivityIds, "recovery_end");
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "end");
    }
}
