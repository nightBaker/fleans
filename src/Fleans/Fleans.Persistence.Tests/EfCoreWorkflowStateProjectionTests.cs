using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests;

/// <summary>
/// The read model holds a running instance's row only; its details are written once it completes.
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class EfCoreWorkflowStateProjectionTests : PersistenceTestBase
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowInstanceState RunningInstance(Guid instanceId)
    {
        var state = new WorkflowInstanceState();
        state.Initialize(instanceId, null, Guid.NewGuid(), At, businessId: "order-1");
        state.Start(At);
        state.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "task1", instanceId)]);
        return state;
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_RunningInstance_WritesOnlyItsRow(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);

        // Act
        await projection.WriteAsync(instanceId.ToString(), state, version: 3);

        // Assert
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances.SingleAsync(s => s.Id == instanceId);
        Assert.IsTrue(row.IsStarted);
        Assert.AreEqual("order-1", row.BusinessId);
        Assert.AreEqual(3, await projection.ReadVersionAsync(instanceId.ToString()));
        Assert.AreEqual(0, await db.Set<ActivityInstanceEntry>().CountAsync(e => e.WorkflowInstanceId == instanceId));
        Assert.AreEqual(0, await db.Set<WorkflowVariablesState>().CountAsync(v => v.WorkflowInstanceId == instanceId));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_CompletedInstance_WritesItsDetailsOnce(PersistenceProvider provider)
    {
        // Arrange — projected while running, then completed
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        await projection.WriteAsync(instanceId.ToString(), state, version: 3);
        var task = state.Entries.Single();
        state.CompleteEntry(task.ActivityInstanceId, At);
        state.Complete(At);

        // Act — the completing write, and a later one (activation catch-up)
        await projection.WriteAsync(instanceId.ToString(), state, version: 5);
        await projection.WriteAsync(instanceId.ToString(), state, version: 5);

        // Assert
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .SingleAsync(s => s.Id == instanceId);
        Assert.IsTrue(row.IsCompleted);
        Assert.AreEqual(task.ActivityInstanceId, row.Entries.Single().ActivityInstanceId);
        Assert.IsTrue(row.Entries.Single().IsCompleted);
        Assert.HasCount(1, row.VariableStates);
        Assert.AreEqual(5, await projection.ReadVersionAsync(instanceId.ToString()));
    }
}
