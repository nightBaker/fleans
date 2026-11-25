using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Loops through elements that keep state between passes: joins, catch events, user tasks with a
/// boundary. "A loop creates a new activity instance" (CLAUDE.md, at-most-once invariant) must hold
/// for each of them — a pass must not see the previous pass's tokens, subscriptions or timers.
/// Every loop here goes back through an exclusive gateway whose "again" flow is always true
/// (the test silo's condition evaluator), so the test decides how many passes it drives.
/// </summary>
[TestClass]
public class LoopTests : WorkflowTestBase
{
    private const int Passes = 3;

    // --- Joins: the simple loop of #839 was fixed in #1052 and tested only for the parallel join ---

    [TestMethod]
    public async Task InclusiveJoin_InALoop_FiresOnEveryPass()
    {
        // Arrange — t0 → inclusive fork (both true) → (a, b) → inclusive join → xor → back to t0
        var workflow = ForkJoinLoop("inclusive-join-loop",
            new InclusiveGateway("fork", IsFork: true), new InclusiveGateway("join", IsFork: false),
            conditionalBranches: true);

        // Act + Assert
        await DriveForkJoinLoop(workflow);
    }

    [TestMethod]
    public async Task ComplexJoin_InALoop_FiresOnEveryPass()
    {
        // Arrange — t0 → parallel fork → (a, b) → complex join (waits for all) → xor → back to t0
        var workflow = ForkJoinLoop("complex-join-loop",
            new ParallelGateway("fork", IsFork: true), new ComplexGateway("join", IsFork: false, ActivationCondition: null),
            conditionalBranches: false);

        // Act + Assert
        await DriveForkJoinLoop(workflow);
    }

    private async Task DriveForkJoinLoop(WorkflowDefinition workflow)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);

        for (var pass = 1; pass <= Passes; pass++)
        {
            await instance.CompleteActivity("t0", new ExpandoObject());
            await WaitForCondition(instanceId, s =>
                s.ActiveActivityIds.Contains("a") && s.ActiveActivityIds.Contains("b"));
            await instance.CompleteActivity("a", new ExpandoObject());
            await instance.CompleteActivity("b", new ExpandoObject());

            var snapshot = await WaitForCondition(instanceId,
                s => s.CompletedActivityIds.Count(id => id == "again") == pass);
            Assert.AreEqual("t0", snapshot.ActiveActivityIds.Single(), $"pass {pass}: only t0 is active again");
            Assert.AreEqual(pass, snapshot.CompletedActivityIds.Count(id => id == "join"), $"pass {pass}: the join fired once per pass");
        }
    }

    private static WorkflowDefinition ForkJoinLoop(string id, Gateway fork, Gateway join, bool conditionalBranches)
    {
        var start = new StartEvent("start");
        var t0 = new TaskActivity("t0");
        var a = new TaskActivity("a");
        var b = new TaskActivity("b");
        var again = new ExclusiveGateway("again");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, t0, fork, a, b, join, again, end],
            SequenceFlows =
            [
                new SequenceFlow("in", start, t0),
                new SequenceFlow("toFork", t0, fork),
                conditionalBranches ? new ConditionalSequenceFlow("toA", fork, a, "true") : new SequenceFlow("toA", fork, a),
                conditionalBranches ? new ConditionalSequenceFlow("toB", fork, b, "true") : new SequenceFlow("toB", fork, b),
                new SequenceFlow("aToJoin", a, join),
                new SequenceFlow("bToJoin", b, join),
                new SequenceFlow("toAgain", join, again),
                new ConditionalSequenceFlow("loop", again, t0, "true"),
                new ConditionalSequenceFlow("done", again, end, "false")
            ]
        };
    }

    // --- Catch events (#847, #853) ---

    [TestMethod]
    public async Task MessageCatch_InALoop_ReceivesOneMessagePerPass()
    {
        // Arrange — start → catch(order/<orderId>) → xor → back to catch
        var orderId = $"loop-{Guid.NewGuid():N}";
        var workflow = CatchLoop("message-catch-loop",
            new MessageIntermediateCatchEvent("catch", "msg"),
            messages: [new MessageDefinition("msg", "loopMessage", "orderId")]);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        dynamic vars = new ExpandoObject();
        vars.orderId = orderId;
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);

        for (var pass = 1; pass <= Passes; pass++)
        {
            // Act
            Assert.IsTrue(await DeliverMessageWhenSubscribed("loopMessage", orderId), $"pass {pass}: the catch is subscribed");

            // Assert — exactly one delivery per pass, and the next pass waits on a new catch instance
            var snapshot = await WaitForCondition(instanceId,
                s => s.CompletedActivityIds.Count(id => id == "catch") == pass && s.ActiveActivityIds.Contains("catch"));
            Assert.AreEqual(1, snapshot.ActiveActivityIds.Count(id => id == "catch"), $"pass {pass}");
            Assert.IsFalse(snapshot.IsFailed, $"pass {pass}: re-subscribing the same key must not fail the catch");
            Assert.AreEqual(pass, snapshot.CompletedActivityIds.Count(id => id == "again"));
        }
    }

    [TestMethod]
    public async Task SignalCatch_InALoop_IsReachedByOneBroadcastPerPass()
    {
        // Arrange — start → catch(signal) → xor → back to catch
        var signalName = $"loopSignal-{Guid.NewGuid():N}";
        var workflow = CatchLoop("signal-catch-loop",
            new SignalIntermediateCatchEvent("catch", "sig"),
            signals: [new SignalDefinition("sig", signalName)]);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);

        for (var pass = 1; pass <= Passes; pass++)
        {
            // Act
            Assert.AreEqual(1, await BroadcastSignalWhenSubscribed(signalName), $"pass {pass}: one subscriber");

            // Assert
            var snapshot = await WaitForCondition(instanceId,
                s => s.CompletedActivityIds.Count(id => id == "catch") == pass && s.ActiveActivityIds.Contains("catch"));
            Assert.AreEqual(1, snapshot.ActiveActivityIds.Count(id => id == "catch"), $"pass {pass}");
            Assert.IsFalse(snapshot.IsFailed, $"pass {pass}");
        }
    }

    private static WorkflowDefinition CatchLoop(string id, Activity catchEvent,
        List<MessageDefinition>? messages = null, List<SignalDefinition>? signals = null)
    {
        var start = new StartEvent("start");
        var again = new ExclusiveGateway("again");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, catchEvent, again, end],
            SequenceFlows =
            [
                new SequenceFlow("in", start, catchEvent),
                new SequenceFlow("toAgain", catchEvent, again),
                new ConditionalSequenceFlow("loop", again, catchEvent, "true"),
                new ConditionalSequenceFlow("done", again, end, "false")
            ],
            Messages = messages ?? [],
            Signals = signals ?? []
        };
    }

    // --- User task with a timer boundary ---

    [TestMethod]
    public async Task UserTaskWithTimerBoundary_InALoop_AnEarlierPassesTimerDoesNotInterruptTheNewPass()
    {
        // Arrange — start → userTask (interrupting timer → timedOut) → xor → back to userTask
        var start = new StartEvent("start");
        var userTask = new UserTask("ut", "john", [], [], null);
        var timer = new BoundaryTimerEvent("utTimer", "ut", new TimerDefinition(TimerType.Duration, "PT30M"));
        var timedOut = new TaskActivity("timedOut");
        var again = new ExclusiveGateway("again");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "user-task-timer-loop",
            Activities = [start, userTask, timer, timedOut, again, end],
            SequenceFlows =
            [
                new SequenceFlow("in", start, userTask),
                new SequenceFlow("toAgain", userTask, again),
                new ConditionalSequenceFlow("loop", again, userTask, "true"),
                new ConditionalSequenceFlow("done", again, end, "false"),
                new SequenceFlow("onTimeout", timer, timedOut)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);

        var firstPass = await ActiveUserTask(instanceId);
        await instance.ClaimUserTask(firstPass, "john", []);
        await instance.CompleteUserTask(firstPass, "john", new ExpandoObject());
        var secondPass = await ActiveUserTask(instanceId, except: firstPass);

        // Act — the first pass's reminder fires late
        await instance.HandleTimerFired("utTimer", firstPass);

        // Assert — the second pass is untouched
        var snapshot = (await QueryService.GetStateSnapshot(instanceId))!;
        Assert.AreEqual(secondPass, snapshot.ActiveActivities.Single(a => a.ActivityId == "ut").ActivityInstanceId);
        Assert.IsFalse(snapshot.ActiveActivityIds.Contains("timedOut"), "a stale timer must not take the boundary path");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "ut" && a.IsCancelled));

        // Act — the second pass's own timer fires
        await instance.HandleTimerFired("utTimer", secondPass);

        // Assert — only the second pass is interrupted, and its user task leaves the task list
        snapshot = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("timedOut"));
        var cancelled = snapshot.CompletedActivities.Single(a => a.ActivityId == "ut" && a.IsCancelled);
        Assert.AreEqual(secondPass, cancelled.ActivityInstanceId);
        Assert.IsFalse(snapshot.ActiveActivityIds.Contains("ut"));
        Assert.AreEqual(0, (await QueryService.GetActiveUserTasksForWorkflow(instanceId)).Count,
            "the interrupted user task must leave the task list");
    }

    private async Task<Guid> ActiveUserTask(Guid instanceId, Guid? except = null)
    {
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Any(a => a.ActivityId == "ut" && a.ActivityInstanceId != except));
        return snapshot.ActiveActivities.Single(a => a.ActivityId == "ut").ActivityInstanceId;
    }
}
