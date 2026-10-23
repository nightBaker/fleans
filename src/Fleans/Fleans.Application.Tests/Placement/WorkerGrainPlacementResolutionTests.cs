using System.Reflection;
using Fleans.Application.Grains;
using Fleans.Application.Placement;
using Fleans.Worker.Conditions;
using Fleans.Worker.Placement;
using Fleans.Worker.Scripts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// #857 regression: <c>ScriptExecutorGrain</c> and <c>ConditionExpressionEvaluatorGrain</c>
/// must resolve, inside a running silo, to Orleans' <c>StatelessWorkerPlacement</c> (so Orleans
/// gives them the stateless-worker activator — many local activations) while still carrying
/// the worker-role placement filter. Before the fix <c>[WorkerPlacement]</c> was a second
/// placement strategy that overwrote <c>[StatelessWorker]</c>, leaving one activation for the
/// whole cluster.
/// </summary>
[TestClass]
public class WorkerGrainPlacementResolutionTests : WorkflowTestBase
{
    private static readonly Type[] WorkerGrains =
        [typeof(ScriptExecutorGrain), typeof(ConditionExpressionEvaluatorGrain)];

    [TestMethod]
    public void WorkerGrains_ResolveToStatelessWorkerPlacement()
    {
        var services = ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services;
        var typeResolver = services.GetRequiredService<GrainTypeResolver>();
        var strategyResolver = services.GetRequiredService<PlacementStrategyResolver>();

        foreach (var grainClass in WorkerGrains)
        {
            var strategy = strategyResolver.GetPlacementStrategy(typeResolver.GetGrainType(grainClass));

            // StatelessWorkerPlacement is internal to Orleans; the activator check is
            // `PlacementStrategy is StatelessWorkerPlacement`, so compare the exact type name.
            Assert.AreEqual("StatelessWorkerPlacement", strategy.GetType().Name,
                $"{grainClass.Name} resolved to {strategy.GetType().Name}; it must be a stateless worker " +
                "so the cluster runs scripts/conditions on many activations, not one (#857).");
        }
    }

    [TestMethod]
    public void WorkerGrains_CarryTheWorkerRolePlacementFilter()
    {
        var services = ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services;
        var typeResolver = services.GetRequiredService<GrainTypeResolver>();
        var filterResolver = services.GetRequiredService<PlacementFilterStrategyResolver>();

        foreach (var grainClass in WorkerGrains)
        {
            // Also proves the filter is registered on the silo: the resolver throws
            // KeyNotFoundException for an unregistered filter.
            var filters = filterResolver.GetPlacementFilterStrategies(typeResolver.GetGrainType(grainClass));

            Assert.IsTrue(filters.Any(f => f is WorkerPlacementFilterStrategy),
                $"{grainClass.Name} must keep the [WorkerPlacement] filter so it runs only on worker-/combined- silos.");
        }
    }

    [TestMethod]
    public void NoGrainClass_CombinesTwoPlacementStrategies()
    {
        // Orleans keeps only the last placement attribute (they all write the same
        // 'placement-strategy' grain property), so a second one silently drops the first.
        // Role restrictions must be placement *filters* instead.
        var assemblies = new[]
        {
            typeof(WorkflowInstance).Assembly,          // Fleans.Application
            typeof(CorePlacementAttribute).Assembly,    // Fleans.Application.Abstractions
            typeof(ScriptExecutorGrain).Assembly,       // Fleans.Worker
        };

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IGrain).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<PlacementAttribute>(inherit: true).Count() > 1)
            .Select(t => t.FullName)
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            "Grain classes with more than one placement attribute: " + string.Join(", ", offenders));
    }
}
