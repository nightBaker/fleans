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
    // Sub-microsecond ticks: finer than Postgres stores, as the clock produces them.
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    private static WorkflowInstanceState RunningInstance(Guid instanceId)
    {
        var state = new WorkflowInstanceState();
        state.Initialize(instanceId, null, Guid.NewGuid(), At, businessId: "order-1");
        state.SetParentInfo(Guid.NewGuid(), "callActivity");
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
        await projection.WriteAsync(instanceId.ToString(), state);

        // Assert
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances.SingleAsync(s => s.Id == instanceId);
        Assert.IsTrue(row.IsStarted);
        Assert.AreEqual("order-1", row.BusinessId);
        Assert.AreEqual(0, await db.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == instanceId));
        Assert.AreEqual(0, await db.WorkflowVariableStates.CountAsync(v => v.WorkflowInstanceId == instanceId));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadRow_ReturnsTheWrittenRow_EqualToTheStates(PersistenceProvider provider)
    {
        // Arrange — inserted, then updated in place
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = new WorkflowInstanceState();
        state.Initialize(instanceId, null, Guid.NewGuid(), At, businessId: "order-1");
        await projection.WriteAsync(instanceId.ToString(), state);
        state.SetParentInfo(Guid.NewGuid(), "callActivity");
        state.Start(At);

        // Act
        await projection.WriteAsync(instanceId.ToString(), state);
        var row = await projection.ReadRowAsync(instanceId.ToString());

        // Assert — the activation repair compares exactly these: equal means no rewrite
        Assert.AreEqual(WorkflowInstanceRow.From(state), row);
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.WorkflowInstances.CountAsync(s => s.Id == instanceId));
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
        var row = await projection.ReadRowAsync(Guid.NewGuid().ToString());

        // Assert
        Assert.IsNull(row);
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
        await projection.WriteAsync(instanceId.ToString(), state);
        var task = state.Entries.Single();
        state.CompleteEntry(task.ActivityInstanceId, At);
        state.Complete(At);

        // Act — the completing write, and a later one (activation repair)
        await projection.WriteAsync(instanceId.ToString(), state);
        await projection.WriteAsync(instanceId.ToString(), state);

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
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId.ToString()));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WorkflowInstanceRow_CoversEveryMappedScalarColumn(PersistenceProvider provider)
    {
        // A column mapped on the state but missing from the row would never be written for a
        // running instance, nor compared on activation.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var entityType = db.Model.FindEntityType(typeof(WorkflowInstanceState))!;

        var mapped = entityType.GetProperties()
            .Where(p => !p.IsPrimaryKey())
            .Select(p => p.Name)
            .Order()
            .ToList();
        var row = typeof(WorkflowInstanceRow).GetProperties()
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .Order()
            .ToList();

        CollectionAssert.AreEqual(mapped, row);
    }
}
