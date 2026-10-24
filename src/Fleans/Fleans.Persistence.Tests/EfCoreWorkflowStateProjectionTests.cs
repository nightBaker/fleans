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
        await projection.WriteAsync(instanceId.ToString(), state, version: 1);

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
    public async Task Write_FirstProjectionOfARunningInstance_IsOneInsert(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);

        // Act
        using var counter = EfCommandCounter.Start();
        await projection.WriteAsync(instanceId.ToString(), state, version: 1);
        var commands = counter.Commands;

        // Assert — one round trip per started instance (#903): no update-then-check-then-insert
        Assert.HasCount(1, commands, string.Join(Environment.NewLine, commands));
        StringAssert.StartsWith(commands[0].TrimStart(), "INSERT");
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId.ToString()));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_RunningRowAlreadyThere_UpdatesItOnlyPastItsVersion(PersistenceProvider provider)
    {
        // Arrange — the row was written by an earlier projector activation
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var instanceId = Guid.NewGuid();
        var created = new WorkflowInstanceState();
        created.Initialize(instanceId, null, Guid.NewGuid(), At, businessId: "order-1");
        await new EfCoreWorkflowStateProjection(fixture.CommandFactory).WriteAsync(instanceId.ToString(), created, version: 1);
        var started = RunningInstance(instanceId);

        // Act — a reactivated projector writes the same version again (its insert conflicts),
        // then a newer one, then a stale duplicate repeats the older one
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        await projection.WriteAsync(instanceId.ToString(), created, version: 1);
        await projection.WriteAsync(instanceId.ToString(), started, version: 2);
        await projection.WriteAsync(instanceId.ToString(), created, version: 1);

        // Assert
        Assert.AreEqual(WorkflowInstanceRow.From(started), await projection.ReadRowAsync(instanceId.ToString()));
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var stored = await db.WorkflowInstances.SingleAsync(s => s.Id == instanceId);
        Assert.AreEqual(2, db.Entry(stored).Property<int>(FleanModelConfiguration.ProjectedVersion).CurrentValue);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_CompletingARunningRow_DoesNotLoadDetails(PersistenceProvider provider)
    {
        // Arrange — projected while running: a running row has no details stored
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        await projection.WriteAsync(instanceId.ToString(), state, version: 2);
        state.CompleteEntry(state.Entries.Single().ActivityInstanceId, At);
        state.Complete(At);

        // Act
        using var counter = EfCommandCounter.Start();
        await projection.WriteAsync(instanceId.ToString(), state, version: 5);
        var commands = counter.Commands;

        // Assert — the row is read, the details are only written (#903)
        Assert.IsFalse(
            commands.Any(c => c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && (c.Contains("WorkflowActivityInstanceEntries") || c.Contains("WorkflowVariableStates") || c.Contains("WorkflowConditionSequenceStates"))),
            string.Join(Environment.NewLine, commands));
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == instanceId));
        Assert.AreEqual(1, await db.WorkflowVariableStates.CountAsync(v => v.WorkflowInstanceId == instanceId));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_CompletingARunningRowThatHasDetails_DiffsAgainstThem(PersistenceProvider provider)
    {
        // Arrange — details stored, then the row set back to running at an older version (not
        // something the projection writes, but it must not fail on it forever)
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        state.CompleteEntry(state.Entries.Single().ActivityInstanceId, At);
        state.Complete(At);
        await projection.WriteAsync(instanceId.ToString(), state, version: 5);
        await using (var db = await fixture.CommandFactory.CreateDbContextAsync())
            await db.WorkflowInstances.Where(s => s.Id == instanceId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.IsCompleted, false)
                .SetProperty(s => EF.Property<int>(s, FleanModelConfiguration.ProjectedVersion), 1));

        // Act
        await projection.WriteAsync(instanceId.ToString(), state, version: 6);

        // Assert
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId.ToString()));
        await using var check = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await check.WorkflowActivityInstanceEntries.CountAsync(e => e.WorkflowInstanceId == instanceId));
        Assert.AreEqual(1, await check.WorkflowVariableStates.CountAsync(v => v.WorkflowInstanceId == instanceId));
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
        await projection.WriteAsync(instanceId.ToString(), state, version: 1);
        state.SetParentInfo(Guid.NewGuid(), "callActivity");
        state.Start(At);

        // Act
        await projection.WriteAsync(instanceId.ToString(), state, version: 2);
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
        await projection.WriteAsync(instanceId.ToString(), state, version: 2);
        var task = state.Entries.Single();
        state.CompleteEntry(task.ActivityInstanceId, At);
        state.Complete(At);

        // Act — the completing write, and the same version again (a redelivered notification)
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
        Assert.AreEqual(WorkflowInstanceRow.From(state), await projection.ReadRowAsync(instanceId.ToString()));
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_OlderVersion_DoesNotRollTheRowBack(PersistenceProvider provider)
    {
        // Arrange — the row already reflects version 5 (started, completed)
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var completed = RunningInstance(instanceId);
        completed.CompleteEntry(completed.Entries.Single().ActivityInstanceId, At);
        completed.Complete(At);
        await projection.WriteAsync(instanceId.ToString(), completed, version: 5);
        var older = new WorkflowInstanceState();
        older.Initialize(instanceId, null, Guid.NewGuid(), At, businessId: "order-1");

        // Act — a late write of an older state (a stale projector activation), running and completed
        await projection.WriteAsync(instanceId.ToString(), older, version: 1);
        await projection.WriteAsync(instanceId.ToString(), completed, version: 4);

        // Assert
        Assert.AreEqual(WorkflowInstanceRow.From(completed), await projection.ReadRowAsync(instanceId.ToString()));
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var stored = await db.WorkflowInstances.SingleAsync(s => s.Id == instanceId);
        Assert.AreEqual(5, db.Entry(stored).Property<int>(FleanModelConfiguration.ProjectedVersion).CurrentValue);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_CompletedInstance_NewerVersionUpdatesVariablesAndConditions(PersistenceProvider provider)
    {
        // Arrange — a completed instance with a variable and two condition results
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var projection = new EfCoreWorkflowStateProjection(fixture.CommandFactory);
        var instanceId = Guid.NewGuid();
        var state = RunningInstance(instanceId);
        var gateway = Guid.NewGuid();
        state.AddConditionSequenceStates(gateway, ["flowA", "flowB"]);
        state.SetConditionSequenceResult(gateway, "flowA", true);
        var scope = state.GetRootVariablesId();
        state.MergeState(scope, Variables(("x", 1)));
        state.CompleteEntry(state.Entries.Single().ActivityInstanceId, At);
        state.Complete(At);
        await projection.WriteAsync(instanceId.ToString(), state, version: 5);

        // Act — a post-completion append changed a variable and a condition result
        state.MergeState(scope, Variables(("x", 2)));
        state.SetConditionSequenceResult(gateway, "flowB", true);
        await projection.WriteAsync(instanceId.ToString(), state, version: 6);

        // Assert
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        var row = await db.WorkflowInstances
            .Include(s => s.VariableStates)
            .Include(s => s.ConditionSequenceStates)
            .SingleAsync(s => s.Id == instanceId);
        Assert.AreEqual(2L, Convert.ToInt64(((IDictionary<string, object?>)row.VariableStates.Single().Variables)["x"]));
        Assert.HasCount(2, row.ConditionSequenceStates);
        Assert.IsTrue(row.ConditionSequenceStates.All(c => c.Result));
    }

    private static System.Dynamic.ExpandoObject Variables(params (string Key, object Value)[] values)
    {
        var expando = new System.Dynamic.ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        foreach (var (key, value) in values)
            dict[key] = value;
        return expando;
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
            .Where(p => !p.IsPrimaryKey() && p.Name != FleanModelConfiguration.ProjectedVersion)
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
