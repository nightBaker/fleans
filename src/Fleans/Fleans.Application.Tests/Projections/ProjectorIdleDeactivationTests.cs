using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.Grains;
using Fleans.Application.Projections;
using Fleans.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.Projections;

/// <summary>
/// #1011 — a projector whose instance stops advancing leaves memory after
/// <see cref="ProjectorOptions.IdleDeactivation"/>, instead of staying for Orleans' collection age
/// while its instance waits; one that keeps being notified stays.
/// </summary>
[TestClass]
public class ProjectorIdleDeactivationTests : WorkflowTestBase
{
    private static readonly TimeSpan IdleDeactivation = TimeSpan.FromSeconds(1);
    private const int IdleDeactivationEventId = 4077;

    protected override void ConfigureCluster(TestClusterBuilder builder) =>
        builder.AddSiloBuilderConfigurator<ShortIdleDeactivation>();

    [TestMethod]
    public async Task WaitingInstance_ProjectorDeactivatesAfterIdleWindow_AndCatchesUpWhenTheInstanceAdvances()
    {
        // Arrange — an instance parked in its task, projected
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.StartWorkflow(CreateSimpleWorkflow());
        await WaitForProjection(instanceId);
        Assert.IsTrue(await IsProjectorActive(instanceId), "the projector is active right after projecting");

        // Act — the instance stays parked past the idle window
        await WaitUntil(async () => !await IsProjectorActive(instanceId), TimeSpan.FromSeconds(15));

        // Assert — gone, and the read model still follows the instance once it advances
        Assert.IsTrue(CapturedLogEvents.Count<WorkflowInstanceProjector>(IdleDeactivationEventId) >= 1);
        await grain.CompleteActivity("task", new ExpandoObject());
        await WaitForProjection(instanceId);
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "the reactivated projector projects to the journal's head");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "task"));
    }

    [TestMethod]
    public async Task NotifiedProjector_StaysActive_AndAnOldNotificationAfterReactivationDoesNotRollTheRowBack()
    {
        // Arrange — a parked, projected instance
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.StartWorkflow(CreateSimpleWorkflow());
        await WaitForProjection(instanceId);
        var stream = GetSiloService<IClusterClient>().GetStreamProvider(WorkflowEventStreams.StreamProvider)
            .GetStream<InstanceAdvanced>(StreamId.Create(WorkflowEventStreams.InstanceAdvancedStreamNamespace, instanceId.ToString("D")));

        // Act — notifications keep arriving, more often than the idle window, for three windows
        var until = DateTime.UtcNow + 3 * IdleDeactivation;
        while (DateTime.UtcNow < until)
        {
            await stream.OnNextAsync(new InstanceAdvanced(instanceId, 1, DateTimeOffset.UtcNow));
            await Task.Delay(IdleDeactivation / 4);
        }

        // Assert — still active
        Assert.IsTrue(await IsProjectorActive(instanceId), "a notified projector keeps its activation");

        // Act — once it is idle and gone, an old notification reactivates it
        await WaitUntil(async () => !await IsProjectorActive(instanceId), TimeSpan.FromSeconds(15));
        await stream.OnNextAsync(new InstanceAdvanced(instanceId, 1, DateTimeOffset.UtcNow));
        await WaitUntil(() => IsProjectorActive(instanceId), TimeSpan.FromSeconds(10));

        // Assert — the row is not rolled back to the old version
        await WaitForProjection(instanceId);
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task"),
            "the reactivated projector leaves the row at the journal's head");
    }

    private async Task<bool> IsProjectorActive(Guid instanceId)
    {
        var activations = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        return activations.Any(a =>
            a.GrainType.Contains($".{nameof(WorkflowInstanceProjector)},", StringComparison.Ordinal)
            && a.GrainId.ToString().Contains(instanceId.ToString("D"), StringComparison.Ordinal));
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"condition not met within {timeout}");
            await Task.Delay(100);
        }
    }

    private sealed class ShortIdleDeactivation : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.ConfigureServices(services =>
                services.PostConfigure<ProjectorOptions>(o => o.IdleDeactivation = IdleDeactivation));
    }
}
