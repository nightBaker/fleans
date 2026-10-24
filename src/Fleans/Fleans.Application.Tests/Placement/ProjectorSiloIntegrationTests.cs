using Fleans.Application.Projections;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// #861 — a two-silo cluster: a <c>combined-</c> silo, and a <c>projector-</c> silo set up the way
/// <c>Fleans.Api</c> sets up a <c>Fleans:Role=Projector</c> silo. The projector runs on the
/// projector silo, and the instance grain (and every other Fleans grain) stays off it.
/// </summary>
[TestClass]
public class ProjectorSiloIntegrationTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 2;
        // Compatibility must come from each silo's own manifest, as in production.
        builder.Options.AssumeHomogenousSilosForTesting = false;
        builder.AddSiloBuilderConfigurator<SecondaryIsProjectorSilo>();
    }

    [TestMethod]
    public async Task ReadModel_IsProjectedOnTheProjectorSilo_AndNoOtherFleansGrainRunsThere()
    {
        // Arrange
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("projector-silo")
            .DeployVersion((WorkflowDefinition)CreateSimpleWorkflow("projector-silo"), "<placeholder/>");
        var commands = GetSiloService<IWorkflowCommandService>();

        // Act: the instance waits in its task, so the projector stays active.
        var instanceId = await commands.StartWorkflow("projector-silo");
        await WaitForProjection(instanceId);

        // Assert
        var projectorSilo = Cluster.SecondarySilos.Single().SiloAddress;
        var activations = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();

        var projector = activations.Single(a =>
            a.GrainType.Contains(nameof(WorkflowInstanceProjector)) && a.GrainId.ToString().Contains(instanceId.ToString("D")));
        Assert.AreEqual(projectorSilo, projector.SiloAddress, "The projector must run on the projector silo.");

        var otherFleansGrainsOnProjectorSilo = activations
            .Where(a => a.SiloAddress.Equals(projectorSilo))
            .Where(a => a.GrainType.StartsWith("Fleans.", StringComparison.Ordinal))
            .Where(a => !a.GrainType.Contains(nameof(WorkflowInstanceProjector)))
            .Select(a => a.GrainType)
            .ToList();
        Assert.IsEmpty(otherFleansGrainsOnProjectorSilo,
            $"A projector silo hosts only the projector; found {string.Join(", ", otherFleansGrainsOnProjectorSilo)}");

        var rows = await QueryService.GetInstancesByKey("projector-silo", new PageRequest());
        Assert.AreEqual(instanceId, rows.Items.Single().InstanceId);
    }

    /// <summary>Makes every silo but the primary a projector silo: role prefix and grain set.</summary>
    private sealed class SecondaryIsProjectorSilo : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            if (siloBuilder.Configuration["Orleans:Name"] == "Primary")
                return;

            siloBuilder.Configure<SiloOptions>(o => o.SiloName = "projector-test-" + Guid.NewGuid().ToString("N"));
            siloBuilder.Services.PostConfigure<GrainTypeOptions>(ProjectorSilo.KeepOnlyProjectorGrains);
        }
    }
}
