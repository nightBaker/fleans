using Stopwatch = System.Diagnostics.Stopwatch;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

/// <summary>
/// Per-step latency of a chain of script tasks and of exclusive gateways on an idle cluster. A
/// script step is dispatched straight to the executor grain and a gateway evaluates its conditions
/// in the instance grain, so no step waits for a stream pulling agent's poll (100 ms by default).
/// </summary>
[TestClass]
public class ScriptChainLatencyTests : WorkflowTestBase
{
    private const int Steps = 10;
    private const int Runs = 11;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TenScriptTasks_PerStepLatency_StaysFarBelowTheStreamPollPeriod()
    {
        var perStepMs = await MeasurePerStepMs(BuildScriptChain());

        TestContext.WriteLine($"script chain: p50 {perStepMs:F1} ms per step ({Steps} steps, {Runs} runs)");
        // Each step used to wait for a stream poll (100 ms period); a direct dispatch takes a few ms.
        Assert.IsTrue(perStepMs < 50, $"p50 per script step was {perStepMs:F1} ms");
    }

    [TestMethod]
    public async Task TenExclusiveGateways_PerStepLatency_StaysFarBelowTheStreamPollPeriod()
    {
        var perStepMs = await MeasurePerStepMs(BuildGatewayChain());

        TestContext.WriteLine($"gateway chain: p50 {perStepMs:F1} ms per step ({Steps} steps, {Runs} runs)");
        Assert.IsTrue(perStepMs < 50, $"p50 per gateway step was {perStepMs:F1} ms");
    }

    private async Task<double> MeasurePerStepMs(IWorkflowDefinition definition)
    {
        // Warm-up: activations, the definition cache, the database.
        await RunToCompletion(definition);

        var samples = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            samples.Add(await RunToCompletion(definition));
            // Idle between runs: the read-model projection of the previous run shares the test SQLite.
            await Task.Delay(500);
        }

        samples.Sort();
        var p50 = samples[samples.Count / 2];
        TestContext.WriteLine($"runs (ms): {string.Join(", ", samples.Select(s => s.ToString("F0")))}");
        return p50 / Steps;
    }

    private async Task<double> RunToCompletion(IWorkflowDefinition definition)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var stopwatch = Stopwatch.StartNew();
        await instance.StartWorkflow(definition);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await instance.GetStateSnapshot()).IsCompleted)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The workflow did not complete");
            await Task.Delay(1);
        }
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static IWorkflowDefinition BuildScriptChain()
    {
        var activities = new List<Activity> { new StartEvent("start") };
        for (var i = 1; i <= Steps; i++)
            activities.Add(new ScriptTask($"script{i}", "_context.x = 1"));
        activities.Add(new EndEvent("end"));
        return Chain("script-chain", activities);
    }

    private static IWorkflowDefinition BuildGatewayChain()
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var deadEnd = new EndEvent("deadEnd");
        var gateways = Enumerable.Range(1, Steps).Select(i => new ExclusiveGateway($"gw{i}")).ToList();

        var activities = new List<Activity> { start };
        activities.AddRange(gateways);
        activities.Add(end);
        activities.Add(deadEnd);

        // Each gateway's "true" flow leads on; its "false" flow never fires.
        var flows = new List<SequenceFlow> { new("in", start, gateways[0]) };
        for (var i = 0; i < Steps; i++)
        {
            Activity next = i + 1 < Steps ? gateways[i + 1] : end;
            flows.Add(new ConditionalSequenceFlow($"true{i}", gateways[i], next, "true"));
            flows.Add(new ConditionalSequenceFlow($"false{i}", gateways[i], deadEnd, "false"));
        }

        return new WorkflowDefinition
        {
            WorkflowId = "gateway-chain",
            Activities = activities,
            SequenceFlows = flows
        };
    }

    private static IWorkflowDefinition Chain(string workflowId, List<Activity> activities)
    {
        var flows = new List<SequenceFlow>();
        for (var i = 0; i < activities.Count - 1; i++)
            flows.Add(new SequenceFlow($"f{i}", activities[i], activities[i + 1]));
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = flows
        };
    }
}
