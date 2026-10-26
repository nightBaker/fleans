using Fleans.Application.Grains;
using Fleans.Application.Tests.CustomTasks.TestStubs;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.InProcess;

namespace Fleans.Application.Tests.CustomTasks;

/// <summary>
/// Two plugin hosts with different plugins in one cluster: host A has only plugin A's handler,
/// host B only plugin B's, the engine silo neither. Each silo drops the other handler classes
/// from its <see cref="GrainTypeOptions"/>, so its grain manifest is what a host without that
/// plugin's DLL advertises. The engine-side stream agents still see both implicit subscriptions
/// through the cluster manifest, and default placement must pick only a silo whose manifest
/// carries the handler — otherwise a task would activate on a host that cannot load it.
/// This relies on each plugin having its own grain type, which the <c>[GrainType]</c> check in
/// <c>AddCustomTaskPlugin&lt;T&gt;</c> enforces: two handlers sharing a class name would otherwise
/// share a grain type, and the instance hangs on a task delivered to the wrong host.
/// </summary>
[TestClass]
[DoNotParallelize]
public class HeterogeneousPluginHostsTests : WorkflowTestBase
{
    private const string HostASilo = "Secondary_1";
    private const string HostBSilo = "Secondary_2";

    protected override void ConfigureCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 3;
        builder.AddSiloBuilderConfigurator<PluginSliceConfigurator>();
    }

    [TestMethod]
    public async Task Each_plugin_runs_only_on_the_host_that_has_it()
    {
        // Arrange — start → ctA (host A's plugin) → ctB (host B's plugin) → end, many instances,
        // so a placement that ignored the manifests would hit a wrong silo almost surely.
        TestHostSliceExecutions.Executions.Clear();
        var start = new StartEvent("start");
        var ctA = new CustomTaskActivity("ctA", "test-host-a", null, null);
        var ctB = new CustomTaskActivity("ctB", "test-host-b", null, null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "custom-task-two-plugin-hosts",
            Activities = [start, ctA, ctB, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ctA),
                new SequenceFlow("f2", ctA, ctB),
                new SequenceFlow("f3", ctB, end)
            ]
        };
        const int instanceCount = 20;
        var instances = Enumerable.Range(0, instanceCount)
            .Select(_ => Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid()))
            .ToList();

        // Act
        await Task.WhenAll(instances.Select(async i => await i.StartWorkflow(workflow)));

        // Assert — every instance completes, each plugin ran once per instance, on its own host only.
        foreach (var instance in instances)
        {
            var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 20000);
            Assert.IsFalse(done.IsFailed, $"Instance {instance.GetPrimaryKey()} failed");
        }

        var hostA = SiloName(HostASilo);
        var hostB = SiloName(HostBSilo);
        var executions = TestHostSliceExecutions.Executions.ToList();
        Assert.AreEqual(instanceCount, executions.Count(e => e.TaskType == "test-host-a"));
        Assert.AreEqual(instanceCount, executions.Count(e => e.TaskType == "test-host-b"));
        CollectionAssert.AreEquivalent(new[] { hostA },
            executions.Where(e => e.TaskType == "test-host-a").Select(e => e.SiloName).Distinct().ToList(),
            "Plugin A must run only on host A.");
        CollectionAssert.AreEquivalent(new[] { hostB },
            executions.Where(e => e.TaskType == "test-host-b").Select(e => e.SiloName).Distinct().ToList(),
            "Plugin B must run only on host B.");
    }

    private string SiloName(string testSiloName) =>
        ((InProcessSiloHandle)Cluster.GetActiveSilos().Single(s => s.Name == testSiloName))
            .SiloHost.Services.GetRequiredService<ILocalSiloDetails>().Name;

    private sealed class PluginSliceConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            var testSiloName = hostBuilder.Configuration["Orleans:Name"];
            var excluded = testSiloName switch
            {
                HostASilo => new[] { typeof(TestHostBCustomTaskHandler) },
                HostBSilo => new[] { typeof(TestHostACustomTaskHandler) },
                _ => new[] { typeof(TestHostACustomTaskHandler), typeof(TestHostBCustomTaskHandler) },
            };
            hostBuilder.Services.PostConfigure<GrainTypeOptions>(o =>
            {
                foreach (var type in excluded)
                    o.Classes.Remove(type);
            });
        }
    }
}
