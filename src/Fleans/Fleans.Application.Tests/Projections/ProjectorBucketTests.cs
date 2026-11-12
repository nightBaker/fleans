using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.Projections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.Projections;

/// <summary>
/// #1011 — instances are projected by <see cref="ProjectorOptions.BucketCount"/> bucket projectors,
/// not one projector per instance: many instances advancing at once activate no more projectors
/// than there are buckets, and each one's read model still reaches its journal's head.
/// </summary>
[TestClass]
public class ProjectorBucketTests : WorkflowTestBase
{
    private const int BucketCount = 2;

    protected override void ConfigureCluster(TestClusterBuilder builder) =>
        builder.AddSiloBuilderConfigurator<TwoBuckets>();

    [TestMethod]
    public async Task ManyInstances_AreProjectedByTheBucketProjectors_Only()
    {
        // Arrange
        var instanceIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var grains = instanceIds.Select(id => Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(id)).ToList();

        // Act — every instance starts and parks in its task, then all complete at once
        await Task.WhenAll(grains.Select(g => g.StartWorkflow(CreateSimpleWorkflow())));
        await WaitForProjection([.. instanceIds]);
        await Task.WhenAll(grains.Select(g => g.CompleteActivity("task", new ExpandoObject())));
        await WaitForProjection([.. instanceIds]);

        // Assert — at most one projector per bucket, and every read model at its head
        var activations = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        var projectors = activations.Count(a =>
            a.GrainType.Contains($".{nameof(WorkflowInstanceProjector)},", StringComparison.Ordinal));
        Assert.IsTrue(projectors is >= 1 and <= BucketCount, $"{projectors} projector activations for {BucketCount} buckets");

        foreach (var instanceId in instanceIds)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.IsCompleted, $"the read model of {instanceId} reached the journal's head");
            Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "task"));
        }
    }

    private sealed class TwoBuckets : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.ConfigureServices(services =>
                services.PostConfigure<ProjectorOptions>(o => o.BucketCount = BucketCount));
    }
}
