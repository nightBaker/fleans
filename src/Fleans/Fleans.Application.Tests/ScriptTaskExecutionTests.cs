using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

/// <summary>
/// A script task is dispatched straight to the script executor grain, which calls back
/// <c>CompleteActivity</c> / <c>FailActivity</c>. A dispatch that gets no result within
/// <c>Fleans:Worker:ScriptResultTimeout</c> (1 s here) is sent again — and so is every executing
/// script when the instance activates — and the task fails after
/// <see cref="WorkerEvaluatorOptions.ScriptDispatchAttempts"/> dispatches. Only the first result
/// is applied. Scripts understand: <c>SET:name=value</c>, <c>FAIL</c>, <c>HANG:key</c> (every run
/// takes 8 s) and <c>HANG_FIRST:key</c> (the first run takes 8 s, later ones return at once).
/// </summary>
[TestClass]
public class ScriptTaskExecutionTests : WorkflowTestBase
{
    private static readonly TimeSpan HangTime = TimeSpan.FromSeconds(8);
    private static readonly ConcurrentDictionary<string, int> Runs = new();

    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<ScriptSiloConfigurator>();

    [TestMethod]
    public async Task ScriptTask_Completes_AndMergesTheScriptsVariables()
    {
        // Arrange
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(ScriptWorkflow("SET:x=10"));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        // Assert
        Assert.IsFalse(snapshot.IsFailed);
        var script = snapshot.CompletedActivities.Single(a => a.ActivityId == "script1");
        Assert.IsNull(script.ErrorState);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
        Assert.AreEqual("10", (await instance.GetVariable(script.VariablesStateId, "x"))?.ToString());
    }

    [TestMethod]
    public async Task ScriptTask_Fails_WhenTheScriptThrows()
    {
        // Arrange
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(ScriptWorkflow("FAIL"));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        // Assert — the activity carries the script's error; the end event was never reached
        var script = snapshot.CompletedActivities.Single(a => a.ActivityId == "script1");
        Assert.IsNotNull(script.ErrorState);
        StringAssert.Contains(script.ErrorState.Message, "Simulated script failure");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task ScriptTask_WithoutAResultInTime_IsDispatchedAgain_AndTheLateResultIsDropped()
    {
        // Arrange — the first run hangs past the result timeout, the second returns at once
        var script = $"HANG_FIRST:{Guid.NewGuid():N}";
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(ScriptWorkflow(script));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 3500);

        // Assert — completed by the second dispatch, before the first run returned
        Assert.AreEqual(2, Runs[script]);
        Assert.IsFalse(snapshot.IsFailed);

        // The first run's result arrives after completion and is ignored: the script task and the
        // end event completed once.
        await Task.Delay(HangTime);
        snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "script1"));
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "end"));
    }

    [TestMethod]
    public async Task ScriptTask_WithNoResultAfterEveryDispatch_FailsWithATimeout()
    {
        // Arrange — every run hangs past the result timeout
        var script = $"HANG:{Guid.NewGuid():N}";
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(ScriptWorkflow(script));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        // Assert — failed after the last dispatch
        Assert.AreEqual(WorkerEvaluatorOptions.ScriptDispatchAttempts, Runs[script]);
        var task = snapshot.CompletedActivities.Single(a => a.ActivityId == "script1");
        Assert.IsNotNull(task.ErrorState);
        StringAssert.Contains(task.ErrorState.Message, "got no result");

        // The hung runs' results arrive later and change nothing.
        await Task.Delay(HangTime);
        snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task ExecutingScriptTask_IsDispatchedAgain_WhenTheInstanceActivates()
    {
        // Arrange — the first run hangs; the instance goes away before its result arrives
        var script = $"HANG_FIRST:{Guid.NewGuid():N}";
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var redispatchedBefore = CapturedLogEvents.Count<WorkflowInstance>(1167);
        // Deployed, so that the next activation can load the definition.
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("script-execution");
        await processGrain.DeployVersion((WorkflowDefinition)ScriptWorkflow(script), "<placeholder/>");
        await instance.StartWorkflow((await processGrain.GetStartableDefinition())!);
        await ForceAllGrainDeactivation(instance.GetPrimaryKey());

        // Act — any call activates it again
        await instance.GetStateSnapshot();
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 3500);

        // Assert — the new activation dispatched the executing script, whose run completed it
        Assert.IsTrue(CapturedLogEvents.Count<WorkflowInstance>(1167) > redispatchedBefore,
            "The activation should log that it dispatched the executing script again (1167)");
        Assert.IsTrue(Runs[script] >= 2);
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "script1"));
    }

    private static IWorkflowDefinition ScriptWorkflow(string script)
    {
        var start = new StartEvent("start");
        var task = new ScriptTask("script1", script);
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = "script-execution",
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
    }

    private sealed class ScriptSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IScriptExpressionExecutor, TestScriptExecutor>();
            services.Configure<WorkerEvaluatorOptions>(o => o.ScriptResultTimeout = TimeSpan.FromSeconds(1));
        });
    }

    private sealed class TestScriptExecutor : IScriptExpressionExecutor
    {
        public async Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat)
        {
            var run = Runs.AddOrUpdate(script, 1, (_, n) => n + 1);
            if (script == "FAIL")
                throw new InvalidOperationException("Simulated script failure");
            if (script.StartsWith("SET:", StringComparison.Ordinal))
            {
                var assignment = script["SET:".Length..].Split('=', 2);
                ((IDictionary<string, object?>)variables)[assignment[0]] = assignment[1];
            }
            if (script.StartsWith("HANG:", StringComparison.Ordinal)
                || (script.StartsWith("HANG_FIRST:", StringComparison.Ordinal) && run == 1))
                await Task.Delay(HangTime);
            return variables;
        }
    }
}
