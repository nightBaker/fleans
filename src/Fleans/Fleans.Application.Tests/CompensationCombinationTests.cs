using Fleans.Application.Conditions;
using Fleans.Application.Grains;
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
/// Compensation combined with other constructs: a compensable sub-process, a task that ran twice in a
/// loop, and a multi-instance activity. Each completed run must be compensated once, with its own data.
/// Conditions run on the real C# evaluator.
/// </summary>
[TestClass]
public class CompensationCombinationTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<CSharpSiloConfigurator>();

    [TestMethod]
    [Ignore("Blocked by #1124: a compensation boundary on a sub-process is skipped by the walk; the instance completes without running the handler.")]
    public async Task CompensableSubProcess_IsCompensatedAsAWhole_ByAThrowAfterIt()
    {
        // Arrange — start → book(sub: reserve) [compensated by cancel_booking] → throw → end
        var subStart = new StartEvent("subStart");
        var reserve = new TaskActivity("reserve");
        var subEnd = new EndEvent("subEnd");
        var book = new SubProcess("book")
        {
            Activities = [subStart, reserve, subEnd],
            SequenceFlows = [new SequenceFlow("sf1", subStart, reserve), new SequenceFlow("sf2", reserve, subEnd)]
        };
        var start = new StartEvent("start");
        var cancelBooking = new TaskActivity("cancel_booking");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "compensable-sub-process",
            Activities = [start, book, throwComp, end, cancelBooking, new CompensationBoundaryEvent("cb_book", "book", "cancel_booking")],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, book),
                new SequenceFlow("f2", book, throwComp),
                new SequenceFlow("f3", throwComp, end)
            ]
        };
        var instance = await Start(workflow);
        await instance.CompleteActivity("reserve", Vars("bookingId", "B-1"));

        // Act
        var compensating = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("cancel_booking"));
        var handler = compensating.ActiveActivities.Single(a => a.ActivityId == "cancel_booking");

        // Assert
        Assert.AreEqual("B-1", (await instance.GetVariable(handler.VariablesStateId, "bookingId"))?.ToString(),
            "the handler sees what the sub-process produced");
        await instance.CompleteActivity("cancel_booking", new ExpandoObject());
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.AreEqual(1, done.CompletedActivities.Count(a => a.ActivityId == "cancel_booking"), "compensated once");
        CollectionAssert.Contains(done.CompletedActivityIds, "end");
    }

    [TestMethod]
    [Ignore("Blocked by #841: the walk picks the newest snapshot but marks the oldest one compensated, and the parent's current variables win over the snapshot, so both runs see the last paymentId.")]
    public async Task TaskThatRanTwiceInALoop_IsCompensatedOncePerRun_EachWithItsOwnData()
    {
        // Arrange — start → charge [compensated by refund] → again? (loop until done) → throw → end
        var start = new StartEvent("start");
        var charge = new TaskActivity("charge");
        var again = new ExclusiveGateway("again");
        var refund = new TaskActivity("refund");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "compensate-loop",
            Activities = [start, charge, again, throwComp, end, refund, new CompensationBoundaryEvent("cb_charge", "charge", "refund")],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, charge),
                new SequenceFlow("f2", charge, again),
                new ConditionalSequenceFlow("done", again, throwComp, "_context.done == true"),
                new DefaultSequenceFlow("loop", again, charge),
                new SequenceFlow("f3", throwComp, end)
            ]
        };
        var instance = await Start(workflow);
        var instanceId = instance.GetPrimaryKey();
        await instance.CompleteActivity("charge", Vars("paymentId", "P-1", done: false));
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("charge"));
        await instance.CompleteActivity("charge", Vars("paymentId", "P-2", done: true));

        // Act + Assert — handlers run newest first, each with its own run's paymentId
        var refunded = new List<string?>();
        for (var run = 1; run <= 2; run++)
        {
            var expected = run - 1;
            var compensating = await WaitForCondition(instanceId,
                s => s.ActiveActivityIds.Contains("refund") && s.CompletedActivities.Count(a => a.ActivityId == "refund") == expected);
            var handler = compensating.ActiveActivities.Single(a => a.ActivityId == "refund");
            refunded.Add((await instance.GetVariable(handler.VariablesStateId, "paymentId"))?.ToString());
            await instance.CompleteActivity("refund", new ExpandoObject());
        }

        Assert.AreEqual("P-2,P-1", string.Join(",", refunded));
        var done = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(2, done.CompletedActivities.Count(a => a.ActivityId == "refund"));
    }

    [TestMethod]
    [Ignore("Blocked by #841: multi-instance iteration snapshots are recorded under the host's scope, which the walk never searches, so no iteration is compensated.")]
    public async Task MultiInstanceActivity_EachIterationIsCompensated()
    {
        // Arrange — start → charge x3 (parallel MI, script) [compensated by refund] → throw → end
        var charge = new MultiInstanceActivity("charge", new ScriptTask("charge", "_context.charged = true", "csharp"),
            IsSequential: false, LoopCardinality: 3);
        var start = new StartEvent("start");
        var refund = new ScriptTask("refund", "_context.refunded = true", "csharp");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "compensate-multi-instance",
            Activities = [start, charge, throwComp, end, refund, new CompensationBoundaryEvent("cb_charge", "charge", "refund")],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, charge),
                new SequenceFlow("f2", charge, throwComp),
                new SequenceFlow("f3", throwComp, end)
            ]
        };

        // Act
        var instance = await Start(workflow);

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsFalse(done.IsFailed);
        Assert.AreEqual(3, done.CompletedActivities.Count(a => a.ActivityId == "refund"), "one refund per completed iteration");
    }

    // --- helpers ---

    private async Task<IWorkflowInstanceGrain> Start(WorkflowDefinition workflow)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        return instance;
    }

    private static ExpandoObject Vars(string key, string value, bool? done = null)
    {
        var vars = new ExpandoObject();
        var dict = (IDictionary<string, object?>)vars;
        dict[key] = value;
        if (done is not null)
            dict["done"] = done.Value;
        return vars;
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
