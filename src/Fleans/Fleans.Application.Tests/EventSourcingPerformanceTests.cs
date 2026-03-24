using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Diagnostics;
using System.Dynamic;
using Activity = Fleans.Domain.Activities.Activity;

namespace Fleans.Application.Tests;

[TestClass]
[TestCategory("Performance")]
public class EventSourcingPerformanceTests : WorkflowTestBase
{
    [TestMethod]
    public async Task PerformanceBaseline_ActivationTimeVsEventCount()
    {
        var results = new List<(string Scenario, int EventCount, long ActivationMs)>();

        // Scenario 1: Fresh grain (0 events)
        var freshId = Guid.NewGuid();
        var freshGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(freshId);
        var sw = Stopwatch.StartNew();
        await freshGrain.GetWorkflowInstanceId();
        sw.Stop();
        results.Add(("Fresh grain (0 events)", 0, sw.ElapsedMilliseconds));

        // Scenario 2: Short workflow (~10-15 events)
        var shortId = Guid.NewGuid();
        var shortGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(shortId);
        await shortGrain.SetWorkflow(CreateSimpleWorkflow("perf-simple"));
        await shortGrain.StartWorkflow();
        await shortGrain.CompleteActivity("task", new ExpandoObject());
        await ForceAllGrainDeactivation();

        sw.Restart();
        var reactivatedShort = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(shortId);
        await reactivatedShort.GetWorkflowInstanceId();
        sw.Stop();
        results.Add(("Short workflow (~10-15 events)", -1, sw.ElapsedMilliseconds));

        // Scenario 3: Medium workflow with sequential tasks (~30-50 events)
        var medId = Guid.NewGuid();
        var medGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(medId);
        await medGrain.SetWorkflow(CreateLongSequentialWorkflow(10));
        await medGrain.StartWorkflow();
        for (int i = 1; i <= 10; i++)
            await medGrain.CompleteActivity($"task{i}", new ExpandoObject());
        await ForceAllGrainDeactivation();

        sw.Restart();
        var reactivatedMed = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(medId);
        await reactivatedMed.GetWorkflowInstanceId();
        sw.Stop();
        results.Add(("Medium workflow (10 tasks, ~30-50 events)", -1, sw.ElapsedMilliseconds));

        // Scenario 4: Longer workflow (~60-100 events)
        var longId = Guid.NewGuid();
        var longGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(longId);
        await longGrain.SetWorkflow(CreateLongSequentialWorkflow(25));
        await longGrain.StartWorkflow();
        for (int i = 1; i <= 25; i++)
            await longGrain.CompleteActivity($"task{i}", new ExpandoObject());
        await ForceAllGrainDeactivation();

        sw.Restart();
        var reactivatedLong = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(longId);
        await reactivatedLong.GetWorkflowInstanceId();
        sw.Stop();
        results.Add(("Long workflow (25 tasks, ~60-100 events)", -1, sw.ElapsedMilliseconds));

        // Output results via Console (visible in test output)
        Console.WriteLine("\n=== Event Sourcing Performance Baseline ===");
        Console.WriteLine($"{"Scenario",-50} {"Activation (ms)",15}");
        Console.WriteLine(new string('-', 67));
        foreach (var (scenario, _, ms) in results)
            Console.WriteLine($"{scenario,-50} {ms,15}");
        Console.WriteLine(new string('-', 67));
        Console.WriteLine("Note: Results are machine-specific. Use as relative baseline only.\n");
    }

    private static IWorkflowDefinition CreateLongSequentialWorkflow(int taskCount)
    {
        var activities = new List<Activity>();
        var flows = new List<SequenceFlow>();

        var start = new StartEvent("start");
        activities.Add(start);

        Activity prev = start;
        for (int i = 1; i <= taskCount; i++)
        {
            var task = new TaskActivity($"task{i}");
            activities.Add(task);
            flows.Add(new SequenceFlow($"seq{i}", prev, task));
            prev = task;
        }

        var end = new EndEvent("end");
        activities.Add(end);
        flows.Add(new SequenceFlow($"seq{taskCount + 1}", prev, end));

        return new WorkflowDefinition
        {
            WorkflowId = $"perf-sequential-{taskCount}",
            Activities = activities,
            SequenceFlows = flows
        };
    }
}
