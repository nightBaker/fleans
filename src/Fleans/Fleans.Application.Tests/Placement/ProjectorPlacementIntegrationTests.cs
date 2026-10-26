using Fleans.Application.Projections;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// #861 — a two-silo cluster: a <c>core-</c> silo and a <c>worker-</c> silo. The read-model
/// projector (<c>[WorkerPlacement]</c>) runs on the worker silo, the instance grain stays on core.
/// </summary>
[TestClass]
public class ProjectorPlacementIntegrationTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 2;
        builder.AddSiloBuilderConfigurator<PrimaryIsCoreSecondaryIsWorker>();
    }

    [TestMethod]
    public async Task ReadModel_IsProjectedOnTheWorkerSilo_InstanceStaysOnCore()
    {
        // Arrange
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("projector-placement")
            .DeployVersion((WorkflowDefinition)CreateSimpleWorkflow("projector-placement"), "<placeholder/>");
        var commands = GetSiloService<IWorkflowCommandService>();

        // Act: the instance waits in its task, so both grains stay active.
        var instanceId = await commands.StartWorkflow("projector-placement");
        await WaitForProjection(instanceId);

        // Assert
        var activations = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        SiloAddress SiloOf(string grainClass) => activations.Single(a =>
            a.GrainType.Contains($".{grainClass},", StringComparison.Ordinal)
            // The instance grain's key prints as "N", the projector's (a stream id) as "D".
            && (a.GrainId.ToString().Contains(instanceId.ToString("D"))
                || a.GrainId.ToString().Contains(instanceId.ToString("N")))).SiloAddress;

        Assert.AreEqual(Cluster.SecondarySilos.Single().SiloAddress, SiloOf(nameof(WorkflowInstanceProjector)),
            "The projector must run on the worker silo.");
        Assert.AreEqual(Cluster.Primary.SiloAddress, SiloOf(nameof(WorkflowInstance)),
            "The instance grain must stay on the core silo.");

        var rows = await QueryService.GetInstancesByKey("projector-placement", new PageRequest());
        Assert.AreEqual(instanceId, rows.Items.Single().InstanceId);
    }

    /// <summary>Names the primary silo as a core silo and every other one as a worker silo.</summary>
    private sealed class PrimaryIsCoreSecondaryIsWorker : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var prefix = siloBuilder.Configuration["Orleans:Name"] == "Primary" ? "core-test-" : "worker-test-";
            siloBuilder.Configure<SiloOptions>(o => o.SiloName = prefix + Guid.NewGuid().ToString("N"));
        }
    }
}
