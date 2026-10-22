using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests;

/// <summary>
/// The read model holds a running instance's row only; its details are written once it completes.
/// A row reads back equal to the one written, which is how the grain tells it is current.
/// </summary>
[TestClass]
[TestCategory("Postgres")]
public class EfCoreWorkflowStateProjectionTests : PersistenceTestBase
{
    // Sub-microsecond ticks: PostgreSQL keeps microseconds, the row must still read back equal.
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    private static WorkflowInstanceState RunningInstance(Guid instanceId)
    {
        var state = new WorkflowInstanceState();
        state.Initialize(instanceId, null, Guid.NewGuid(), At, businessId: "order-1");
        state.Start(At.AddTicks(7));
        state.AddEntries([new ActivityInstanceEntry(Guid.NewGuid(), "task1", instanceId)]);
        return state;
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteRow_InsertsThenUpdates_OnlyTheRow(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        var created = WorkflowInstanceRow.From(state) with { IsStarted = false, ExecutionStartedAt = null };

        // Act — the first write inserts, the second updates
        await projection.WriteRowAsync(instanceId, created);
        var afterInsert = await projection.ReadRowAsync(instanceId);
        await projection.WriteRowAsync(instanceId, WorkflowInstanceRow.From(state));

        // Assert
        Assert.AreEqual(created, afterInsert);
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId));
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.WorkflowInstances.CountAsync(s => s.Id == instanceId));
        Assert.AreEqual(0, await db.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == instanceId));
        Assert.AreEqual(0, await db.WorkflowVariableStates.CountAsync(v => v.WorkflowInstanceId == instanceId));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadRow_NoRow_ReturnsNull(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);

        // Act
        var row = await projection.ReadRowAsync(Guid.NewGuid());

        // Assert
        Assert.IsNull(row);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteCompleted_WritesTheRowAndItsDetails(PersistenceProvider provider)
    {
        // Arrange — projected while running, then completed
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        await projection.WriteRowAsync(instanceId, WorkflowInstanceRow.From(state));
        var task = state.Entries.Single();
        state.CompleteEntry(task.ActivityInstanceId, At);
        state.Complete(At.AddTicks(3));

        // Act — the completing write, and a later one (an append after completion)
        await projection.WriteCompletedAsync(instanceId, state);
        await projection.WriteCompletedAsync(instanceId, state);

        // Assert
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId));
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .SingleAsync(s => s.Id == instanceId);
        Assert.IsTrue(row.IsCompleted);
        Assert.AreEqual(task.ActivityInstanceId, row.Entries.Single().ActivityInstanceId);
        Assert.IsTrue(row.Entries.Single().IsCompleted);
        Assert.HasCount(1, row.VariableStates);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteCompleted_WithoutARow_InsertsIt(PersistenceProvider provider)
    {
        // Arrange — the activation catch-up of an instance whose row was lost
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        state.Complete(At);

        // Act
        await projection.WriteCompletedAsync(instanceId, state);

        // Assert
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId));
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == instanceId));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Row_CoversEveryColumnOfTheWorkflowInstancesTable(PersistenceProvider provider)
    {
        // A column the row lacks would never be written for a running instance, and a change to it
        // would not count as a change.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();

        var columns = db.Model.FindEntityType(typeof(WorkflowInstanceState))!
            .GetProperties()
            .Where(p => !p.IsPrimaryKey())
            .Select(p => p.Name)
            .Order()
            .ToList();
        var rowMembers = typeof(WorkflowInstanceRow).GetProperties()
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .Order()
            .ToList();

        CollectionAssert.AreEqual(columns, rowMembers);
    }
}
