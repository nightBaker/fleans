using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Infrastructure.Conditions;
using Fleans.Infrastructure.Scripts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Several live instances of one activity: a parallel multi-instance body that holds a gateway, a
/// waiting catch event, or a boundary on the multi-instance host. Every iteration shares the
/// body's ActivityIds, so anything resolved by ActivityId instead of activity instance lands on the
/// wrong iteration. Conditions run on the real C# evaluator, so a branch can depend on the
/// iteration's own variables.
/// </summary>
[TestClass]
public class MultiInstanceBodyTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<CSharpSiloConfigurator>();

    [TestMethod]
    [Ignore("Blocked by #844: gateway condition results are routed by ActivityId, so the iterations' results land on one gateway instance and two iterations never leave the gateway.")]
    public async Task ParallelSubProcess_WithExclusiveGateway_RoutesEachIterationByItsOwnItem()
    {
        // Arrange — MI over [a, b, c]: route → (item == "a") taskA | default taskB
        var subStart = new StartEvent("subStart");
        var route = new ExclusiveGateway("route");
        var taskA = new TaskActivity("taskA");
        var taskB = new TaskActivity("taskB");
        var endA = new EndEvent("endA");
        var endB = new EndEvent("endB");
        var body = new SubProcess("perItem")
        {
            Activities = [subStart, route, taskA, taskB, endA, endB],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, route),
                new ConditionalSequenceFlow("isA", route, taskA, "_context.item == \"a\""),
                new DefaultSequenceFlow("other", route, taskB),
                new SequenceFlow("sfA", taskA, endA),
                new SequenceFlow("sfB", taskB, endB)
            ]
        };
        var workflow = MiWorkflow("mi-xor-body", new MultiInstanceActivity("perItem", body,
            IsSequential: false, InputCollection: "items", InputDataItem: "item"));
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        dynamic vars = new ExpandoObject();
        vars.items = new List<object> { "a", "b", "c" };

        // Act
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);

        // Assert — one iteration took taskA, two took taskB, each by its own item
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Count(a => a.ActivityId is "taskA" or "taskB") == 3);
        Assert.AreEqual(1, snapshot.ActiveActivityIds.Count(id => id == "taskA"));
        Assert.AreEqual(2, snapshot.ActiveActivityIds.Count(id => id == "taskB"));
        var taskAEntry = snapshot.ActiveActivities.Single(a => a.ActivityId == "taskA");
        Assert.AreEqual("a", (await instance.GetVariable(taskAEntry.VariablesStateId, "item"))?.ToString());
        foreach (var taskBEntry in snapshot.ActiveActivities.Where(a => a.ActivityId == "taskB"))
            Assert.AreNotEqual("a", (await instance.GetVariable(taskBEntry.VariablesStateId, "item"))?.ToString());
        Assert.IsFalse(snapshot.IsFailed);

        // Act — finish every iteration
        foreach (var entry in snapshot.ActiveActivities.Where(a => a.ActivityId is "taskA" or "taskB"))
            await instance.CompleteActivity(entry.ActivityId, entry.ActivityInstanceId, new ExpandoObject());

        // Assert
        var final = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(final.IsFailed);
        CollectionAssert.Contains(final.CompletedActivityIds, "end");
    }

    [TestMethod]
    [Ignore("Blocked by #853: signal subscriptions are keyed by (instance, ActivityId), so iterations 2..n are dropped as duplicates and only one iteration receives the broadcast.")]
    public async Task ParallelSubProcess_WithSignalCatch_OneBroadcastCompletesEveryIteration()
    {
        // Arrange — MI x3: subStart → wait(signal) → subEnd
        var signalName = $"mi-signal-{Guid.NewGuid():N}";
        var subStart = new StartEvent("subStart");
        var wait = new SignalIntermediateCatchEvent("wait", "sig");
        var subEnd = new EndEvent("subEnd");
        var body = new SubProcess("perItem")
        {
            Activities = [subStart, wait, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, wait),
                new SequenceFlow("sf2", wait, subEnd)
            ],
            Signals = [new SignalDefinition("sig", signalName)]
        };
        var workflow = MiWorkflow("mi-signal-body",
            new MultiInstanceActivity("perItem", body, IsSequential: false, LoopCardinality: 3),
            signals: [new SignalDefinition("sig", signalName)]);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Count(id => id == "wait") == 3);

        // Act
        var delivered = await BroadcastSignalWhenSubscribed(signalName, minSubscribers: 3);

        // Assert
        Assert.AreEqual(3, delivered, "every iteration subscribes on its own");
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(3, snapshot.CompletedActivities.Count(a => a.ActivityId == "wait" && !a.IsCancelled));
    }

    [TestMethod]
    [Ignore("Blocked by #842: every iteration re-registers the host's message boundary; the duplicate subscription fails and StartWorkflow throws.")]
    public async Task ParallelTask_WithMessageBoundaryOnTheHost_StartsAndTheBoundaryCancelsEveryIteration()
    {
        // Arrange — MI task x2 with an interrupting message boundary on the host
        var orderId = $"mi-msg-{Guid.NewGuid():N}";
        var start = new StartEvent("start");
        var mi = new MultiInstanceActivity("task1", new TaskActivity("task1"), IsSequential: false, LoopCardinality: 2);
        var boundary = new MessageBoundaryEvent("bmsg", "task1", "msg1");
        var cancelled = new TaskActivity("cancelled");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-message-boundary",
            Activities = [start, mi, boundary, cancelled, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, mi),
                new SequenceFlow("s2", mi, end),
                new SequenceFlow("s3", boundary, cancelled)
            ],
            Messages = [new MessageDefinition("msg1", "miCancel", "orderId")]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        dynamic vars = new ExpandoObject();
        vars.orderId = orderId;

        // Act
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Count(id => id == "task1") == 3);
        Assert.IsFalse(running.IsFailed, "the host subscribes once; the iterations do not subscribe again");
        Assert.IsTrue(await DeliverMessageWhenSubscribed("miCancel", orderId));

        // Assert — the boundary fires once and cancels the host with every iteration
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("cancelled"));
        Assert.IsFalse(snapshot.ActiveActivityIds.Contains("task1"));
        Assert.AreEqual(1, snapshot.ActiveActivityIds.Count(id => id == "cancelled"));
        Assert.AreEqual(3, snapshot.CompletedActivities.Count(a => a.ActivityId == "task1" && a.IsCancelled));
    }

    [TestMethod]
    [Ignore("Blocked by #842: every iteration registers its own copy of the host's timer boundary, so an iteration's timer interrupts that iteration.")]
    public async Task ParallelTask_WithTimerBoundaryOnTheHost_OnlyTheHostsTimerInterrupts()
    {
        // Arrange — MI task x2 with an interrupting timer boundary on the host
        var start = new StartEvent("start");
        var mi = new MultiInstanceActivity("task1", new TaskActivity("task1"), IsSequential: false, LoopCardinality: 2);
        var timer = new BoundaryTimerEvent("btimer", "task1", new TimerDefinition(TimerType.Duration, "PT30M"));
        var timedOut = new TaskActivity("timedOut");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-timer-boundary",
            Activities = [start, mi, timer, timedOut, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, mi),
                new SequenceFlow("s2", mi, end),
                new SequenceFlow("s3", timer, timedOut)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Count(id => id == "task1") == 3);
        var host = running.ActiveActivities.Single(a => a.ActivityId == "task1" && a.ActivityType == nameof(MultiInstanceActivity));
        var iteration = running.ActiveActivities.First(a => a.ActivityId == "task1" && a.ActivityType != nameof(MultiInstanceActivity));

        // Act — a timer keyed by an iteration must not exist; firing one is a no-op
        await instance.HandleTimerFired("btimer", iteration.ActivityInstanceId);

        // Assert
        var afterIterationTimer = (await QueryService.GetStateSnapshot(instanceId))!;
        Assert.AreEqual(3, afterIterationTimer.ActiveActivityIds.Count(id => id == "task1"),
            "an iteration is not a boundary host");
        Assert.IsFalse(afterIterationTimer.ActiveActivityIds.Contains("timedOut"));

        // Act — the host's timer fires
        await instance.HandleTimerFired("btimer", host.ActivityInstanceId);

        // Assert — the host and both iterations are cancelled, and the boundary path runs once
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("timedOut"));
        Assert.IsFalse(snapshot.ActiveActivityIds.Contains("task1"));
        Assert.AreEqual(1, snapshot.ActiveActivityIds.Count(id => id == "timedOut"));
        Assert.AreEqual(3, snapshot.CompletedActivities.Count(a => a.ActivityId == "task1" && a.IsCancelled));
    }

    private static WorkflowDefinition MiWorkflow(string id, MultiInstanceActivity mi, List<SignalDefinition>? signals = null)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, mi, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, mi),
                new SequenceFlow("s2", mi, end)
            ],
            Signals = signals ?? []
        };
    }

    private sealed class CSharpSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IScriptExpressionExecutor, DynamicExpressoScriptExpressionExecutor>();
            services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();
        });
    }
}
