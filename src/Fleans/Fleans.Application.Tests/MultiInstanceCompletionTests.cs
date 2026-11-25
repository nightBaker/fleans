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
/// How a multi-instance activity ends: a sequential completion condition that stops early, one that is never
/// true, one that cannot be evaluated (#849), a failing sequential iteration, and a multi-instance user task.
/// Conditions run on the real C# evaluator.
/// </summary>
[TestClass]
public class MultiInstanceCompletionTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<CSharpSiloConfigurator>();

    [TestMethod]
    public async Task SequentialCompletionCondition_StopsAfterTheIterationThatMeetsIt()
    {
        // Arrange — sequential x5 over a manual task; stop once 2 iterations completed
        var instance = await Start("mi-seq-completion", SequentialWork(5, "_context.nrOfCompletedInstances >= 2"));

        // Act
        await CompleteIteration(instance);
        await CompleteIteration(instance);

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        // The condition is evaluated as an effect after the iteration completes, so the next iteration may
        // already be spawned; it is cancelled, never completed.
        Assert.AreEqual(2, Iterations(done).Count(a => !a.IsCancelled), "only two iterations ran");
        Assert.IsLessThanOrEqualTo(1, Iterations(done).Count(a => a.IsCancelled));
        CollectionAssert.Contains(done.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task CompletionConditionThatIsNeverTrue_RunsEveryIteration()
    {
        // Arrange
        var instance = await Start("mi-seq-never", SequentialWork(3, "_context.nrOfCompletedInstances > 99"));

        // Act
        for (var i = 0; i < 3; i++)
            await CompleteIteration(instance);

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.AreEqual(3, Iterations(done).Count(a => !a.IsCancelled));
        CollectionAssert.Contains(done.CompletedActivityIds, "end");
    }

    [TestMethod]
    [Ignore("Blocked by #849: a completion condition that throws is logged and treated as false, so the activity runs every iteration and completes.")]
    public async Task CompletionConditionThatCannotBeEvaluated_FailsTheMultiInstanceActivity()
    {
        // Arrange — the condition reads a variable that does not exist; the body is a script, so iterations run on their own
        var broken = new MultiInstanceActivity("work", new ScriptTask("work", "_context.ran = true", "csharp"),
            IsSequential: true, LoopCardinality: 2, CompletionCondition: "_context.noSuchVariable.Length > 0");

        // Act
        var instance = await Start("mi-seq-broken", broken);
        var state = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.IsCompleted || s.IsFailed
                 || s.CompletedActivities.Any(a => a.ActivityId == "work" && a.ActivityType == nameof(MultiInstanceActivity)));

        // Assert
        Assert.IsTrue(state.CompletedActivities.Any(a => a.ActivityId == "work" && a.ActivityType == nameof(MultiInstanceActivity) && a.ErrorState is not null),
            "a broken completion condition fails the multi-instance activity instead of being ignored");
        CollectionAssert.DoesNotContain(state.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task SequentialIterationFails_FailsTheHost_AndStopsTheRemainingIterations()
    {
        // Arrange
        var instance = await Start("mi-seq-fail", SequentialWork(3, completionCondition: null));
        await CompleteIteration(instance);
        var second = await ActiveIteration(instance);

        // Act
        await instance.FailActivity("work", second, new InvalidOperationException("iteration 2 failed"));

        // Assert
        var state = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.CompletedActivities.Any(a => a.ActivityId == "work" && a.ActivityType == nameof(MultiInstanceActivity)));
        var host = state.CompletedActivities.Single(a => a.ActivityId == "work" && a.ActivityType == nameof(MultiInstanceActivity));
        Assert.IsNotNull(host.ErrorState, "the failure reaches the host");
        Assert.AreEqual(2, Iterations(state).Count, "the third iteration is never started");
        Assert.IsFalse(state.ActiveActivityIds.Contains("work"));
        CollectionAssert.DoesNotContain(state.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task ParallelMultiInstanceUserTask_EveryIterationIsATaskInTheTaskList_AndTheHostWaitsForAll()
    {
        // Arrange — parallel x2 over a user task assigned to john
        var approve = new MultiInstanceActivity("approve", new UserTask("approve", "john", [], [], null),
            IsSequential: false, LoopCardinality: 2);
        var instance = await Start("mi-user-task", approve);
        var instanceId = instance.GetPrimaryKey();
        var running = await WaitForCondition(instanceId, s => ActiveUserTasks(s).Count == 2);
        var tasks = ActiveUserTasks(running);
        var pending = await QueryService.GetPendingUserTasks("john", null, new PageRequest(1, 100));
        Assert.AreEqual(2, pending.Items.Count(t => t.WorkflowInstanceId == instanceId), "both iterations are in john's task list");

        // Act — complete the first: the host keeps waiting
        await instance.ClaimUserTask(tasks[0], "john", []);
        await instance.CompleteUserTask(tasks[0], "john", new ExpandoObject());
        var mid = await WaitForCondition(instanceId, s => ActiveUserTasks(s).Count == 1);
        CollectionAssert.DoesNotContain(mid.CompletedActivityIds, "end");

        await instance.ClaimUserTask(tasks[1], "john", []);
        await instance.CompleteUserTask(tasks[1], "john", new ExpandoObject());

        // Assert
        var done = await WaitForCondition(instanceId, s => s.IsCompleted);
        CollectionAssert.Contains(done.CompletedActivityIds, "end");
        Assert.AreEqual(0, (await QueryService.GetActiveUserTasksForWorkflow(instanceId)).Count);
    }

    // --- helpers ---

    private static MultiInstanceActivity SequentialWork(int cardinality, string? completionCondition) =>
        new("work", new TaskActivity("work"), IsSequential: true, LoopCardinality: cardinality,
            CompletionCondition: completionCondition);

    private async Task<IWorkflowInstanceGrain> Start(string workflowId, Activity body)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, body, end],
            SequenceFlows = [new SequenceFlow("f1", start, body), new SequenceFlow("f2", body, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        return instance;
    }

    private async Task<Guid> ActiveIteration(IWorkflowInstanceGrain instance)
    {
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "work" && a.ActivityType != nameof(MultiInstanceActivity)));
        return snapshot.ActiveActivities.Single(a => a.ActivityId == "work" && a.ActivityType != nameof(MultiInstanceActivity)).ActivityInstanceId;
    }

    private async Task CompleteIteration(IWorkflowInstanceGrain instance)
    {
        var iteration = await ActiveIteration(instance);
        await instance.CompleteActivity("work", iteration, new ExpandoObject());
        await WaitForCondition(instance.GetPrimaryKey(),
            s => s.CompletedActivities.Any(a => a.ActivityInstanceId == iteration));
    }

    private static List<ActivityInstanceSnapshot> Iterations(InstanceStateSnapshot s) =>
        s.CompletedActivities.Where(a => a.ActivityId == "work" && a.ActivityType != nameof(MultiInstanceActivity)).ToList();

    private static List<Guid> ActiveUserTasks(InstanceStateSnapshot s) =>
        s.ActiveActivities.Where(a => a.ActivityId == "approve" && a.ActivityType != nameof(MultiInstanceActivity))
            .Select(a => a.ActivityInstanceId).ToList();

    private sealed class CSharpSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IScriptExpressionExecutor, DynamicExpressoScriptExpressionExecutor>();
            services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();
        });
    }
}
