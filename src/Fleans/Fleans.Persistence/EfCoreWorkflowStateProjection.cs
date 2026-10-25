using System.Data.Common;
using System.Reflection;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Fleans.Persistence;

/// <summary>
/// Projects workflow instance state to the WorkflowInstances read model tables. Called by the
/// read-model projector (<c>WorkflowInstanceProjector</c>, through <c>ProjectionBatcher</c>) with
/// state it rebuilt from the journal (#861), many instances per call. Each row stores the journal
/// version it reflects (<see cref="FleanModelConfiguration.ProjectedVersion"/>), and a write only
/// lands past it: a repeated or late write never rolls the row back.
/// </summary>
/// <remarks>
/// The running instances of a batch write only their rows, all in one set-based upsert and without
/// reading them first; their details are read from the grain. The completed ones also write the
/// details the read side serves — entries, variable scopes and condition results — in one
/// <c>SaveChanges</c> with their <c>IsCompleted</c>, so a reader that sees the flag sees them.
/// Gateway forks and complex-join states are cleared on completion and timer cycles are never read,
/// so those stay with the grain. The two parts commit separately: a batch that fails after the
/// first is retried as a whole, and the version guard makes the repeated upsert a no-op.
/// </remarks>
public class EfCoreWorkflowStateProjection : IWorkflowStateProjection
{
    private const string ProjectedVersion = FleanModelConfiguration.ProjectedVersion;

    /// <summary>The row's columns, by name: <see cref="WorkflowInstanceRow"/> mirrors the state's mapped scalars.</summary>
    private static readonly PropertyInfo[] RowProperties = typeof(WorkflowInstanceRow).GetProperties();

    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreWorkflowStateProjection(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<WorkflowInstanceRow?> ReadRowAsync(string grainId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = Guid.Parse(grainId);

        return await db.WorkflowInstances
            .Where(s => s.Id == id)
            .Select(s => new WorkflowInstanceRow(
                s.ProcessDefinitionId,
                s.BusinessId,
                s.CreatedAt,
                s.ParentWorkflowInstanceId,
                s.ParentActivityId,
                s.IsStarted,
                s.ExecutionStartedAt,
                s.IsCompleted,
                s.IsCancelled,
                s.IsFailed,
                s.CompletedAt))
            .SingleOrDefaultAsync();
    }

    public Task WriteAsync(string grainId, WorkflowInstanceState state, int version) =>
        WriteBatchAsync([new ProjectionWrite(grainId, state, version)]);

    public async Task WriteBatchAsync(IReadOnlyList<ProjectionWrite> writes)
    {
        if (writes.Count == 0)
            return;

        // The newest projection of each instance: a batch may carry an older and a newer one, and
        // a multi-row upsert must not touch a row twice. Sorted by id, so concurrent batches that
        // share rows lock them in the same order and cannot deadlock each other.
        var latest = writes
            .GroupBy(w => Guid.Parse(w.GrainId))
            .Select(g => (Id: g.Key, Write: g.MaxBy(w => w.Version)!))
            .OrderBy(x => x.Id)
            .ToList();

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var running = latest.Where(x => !x.Write.State.IsCompleted).ToList();
        if (running.Count > 0)
            await UpsertRowsAsync(db, running);

        var completed = latest.Where(x => x.Write.State.IsCompleted).ToList();
        if (completed.Count > 0)
            await WriteCompletedAsync(db, completed);
    }

    /// <summary>
    /// Every running row of the batch in one statement —
    /// <c>INSERT … VALUES (…), (…) ON CONFLICT ("Id") DO UPDATE SET … WHERE "WorkflowInstances"."ProjectedVersion" &lt; excluded."ProjectedVersion"</c>,
    /// which PostgreSQL and SQLite (3.24+) both accept. A row is inserted when absent and updated
    /// only past its stored version, atomically, so it needs no read first and concurrent writers
    /// cannot race. Table and column names come from the EF model, and each parameter is built by
    /// its column's type mapping, so value converters (SQLite's DateTimeOffset-as-string) apply as
    /// in any EF write.
    /// </summary>
    private static async Task UpsertRowsAsync(FleanCommandDbContext db, List<(Guid Id, ProjectionWrite Write)> rows)
    {
        var entityType = db.Model.FindEntityType(typeof(WorkflowInstanceState))!;
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        var tableName = sql.DelimitIdentifier(table.Name, table.Schema);
        var key = entityType.FindPrimaryKey()!.Properties.Single();
        var version = entityType.FindProperty(ProjectedVersion)!;
        var updated = RowProperties.Select(p => entityType.FindProperty(p.Name)!).Append(version).ToList();
        IProperty[] columns = [key, .. updated];
        string Column(IProperty p) => sql.DelimitIdentifier(p.GetColumnName(table)!);

        // Only a factory for parameters: EF runs the statement on its own command.
        using var parameterFactory = db.Database.GetDbConnection().CreateCommand();
        var parameters = new List<DbParameter>(rows.Count * columns.Length);
        var values = new List<string>(rows.Count);
        foreach (var (id, write) in rows)
        {
            var row = WorkflowInstanceRow.From(write.State);
            object?[] rowValues = [id, .. RowProperties.Select(p => p.GetValue(row)), write.Version];
            var placeholders = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                var name = sql.GenerateParameterName($"p{parameters.Count}");
                parameters.Add(columns[i].GetRelationalTypeMapping()
                    .CreateParameter(parameterFactory, name, rowValues[i], columns[i].IsNullable));
                placeholders[i] = name;
            }
            values.Add($"({string.Join(", ", placeholders)})");
        }

        var statement =
            $"INSERT INTO {tableName} ({string.Join(", ", columns.Select(Column))}) " +
            $"VALUES {string.Join(", ", values)} " +
            $"ON CONFLICT ({Column(key)}) DO UPDATE SET {string.Join(", ", updated.Select(c => $"{Column(c)} = excluded.{Column(c)}"))} " +
            $"WHERE {tableName}.{Column(version)} < excluded.{Column(version)}";
        await db.Database.ExecuteSqlRawAsync(statement, parameters);
    }

    /// <summary>
    /// The batch's completed instances: their stored rows, the details of the rows that were already
    /// completed, the diffs, and one <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// Details are only ever written in the same <c>SaveChanges</c> as <c>IsCompleted</c>, so a stored
    /// row that is still running has none to load or diff against — the usual case, the instance's
    /// first completed projection. Only a replayed completion loads them. Should a running row have
    /// details anyway, the insert hits their keys and the batch is written again with every row's
    /// details loaded.
    /// </remarks>
    private static async Task WriteCompletedAsync(
        FleanCommandDbContext db, List<(Guid Id, ProjectionWrite Write)> completed, bool loadAllDetails = false)
    {
        var ids = completed.Select(c => c.Id).ToList();

        // The rows are their own entities, never the projected states: tracking those would make
        // EF add every collection too. The details are added below as the states' own objects; the
        // context is gone once the write returns.
        var stored = await db.WorkflowInstances
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);
        var withDetails = stored.Values.Where(e => loadAllDetails || e.IsCompleted).Select(e => e.Id).ToList();
        if (withDetails.Count > 0)
        {
            // Same tracked rows: the collections fill in on them.
            await db.WorkflowInstances
                .Where(e => withDetails.Contains(e.Id))
                .Include(e => e.Entries)
                .Include(e => e.VariableStates)
                .Include(e => e.ConditionSequenceStates)
                .AsSplitQuery()
                .LoadAsync();
        }

        foreach (var (id, write) in completed)
        {
            var row = WorkflowInstanceRow.From(write.State);
            if (!stored.TryGetValue(id, out var existing))
            {
                existing = AddRow(db, id, row, write.Version);
            }
            else
            {
                var entry = db.Entry(existing);
                if (entry.Property<int>(ProjectedVersion).CurrentValue >= write.Version)
                    continue;
                entry.CurrentValues.SetValues(row);
                entry.Property<int>(ProjectedVersion).CurrentValue = write.Version;
            }

            DiffEntries(db, existing, write.State, id);
            DiffVariableStates(db, existing, write.State, id);
            DiffConditionSequenceStates(db, existing, write.State, id);
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (!loadAllDetails)
        {
            db.ChangeTracker.Clear();
            await WriteCompletedAsync(db, completed, loadAllDetails: true);
        }
    }

    private static WorkflowInstanceState AddRow(FleanCommandDbContext db, Guid id, WorkflowInstanceRow row, int version)
    {
        var entity = new WorkflowInstanceState();
        db.WorkflowInstances.Add(entity);
        var entry = db.Entry(entity);
        entry.CurrentValues.SetValues(row);
        entry.Property(s => s.Id).CurrentValue = id;
        entry.Property<int>(ProjectedVersion).CurrentValue = version;
        return entity;
    }

    private static void DiffEntries(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingDict = existing.GetEntriesByIdCache();
        var incomingDict = incoming.GetEntriesByIdCache();

        // Remove
        foreach (var old in existing.Entries.Where(e => !incomingDict.ContainsKey(e.ActivityInstanceId)))
            db.Remove(old);

        // Add or update
        foreach (var entry in incoming.Entries)
        {
            if (existingDict.TryGetValue(entry.ActivityInstanceId, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(entry);
            }
            else
            {
                db.Entry(entry).Property(e => e.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(entry);
            }
        }
    }

    private static void DiffVariableStates(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        var existingDict = existing.VariableStates.ToDictionary(v => v.Id);
        var incomingDict = incoming.VariableStates.ToDictionary(v => v.Id);

        foreach (var old in existing.VariableStates.Where(v => !incomingDict.ContainsKey(v.Id)))
            db.Remove(old);

        foreach (var vs in incoming.VariableStates)
        {
            if (existingDict.TryGetValue(vs.Id, out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(vs);
            }
            else
            {
                db.Entry(vs).Property(v => v.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(vs);
            }
        }
    }

    /// <summary>Keyed by the table's primary key, like the other diffs — linear, not O(C²).</summary>
    private static void DiffConditionSequenceStates(FleanCommandDbContext db, WorkflowInstanceState existing, WorkflowInstanceState incoming, Guid instanceId)
    {
        static (Guid, string) Key(ConditionSequenceState c) => (c.GatewayActivityInstanceId, c.ConditionalSequenceFlowId);

        var existingDict = existing.ConditionSequenceStates.ToDictionary(Key);
        var incomingKeys = incoming.ConditionSequenceStates.Select(Key).ToHashSet();

        foreach (var old in existing.ConditionSequenceStates.Where(c => !incomingKeys.Contains(Key(c))))
            db.Remove(old);

        foreach (var cs in incoming.ConditionSequenceStates)
        {
            if (existingDict.TryGetValue(Key(cs), out var tracked))
            {
                db.Entry(tracked).CurrentValues.SetValues(cs);
            }
            else
            {
                db.Entry(cs).Property(c => c.WorkflowInstanceId).CurrentValue = instanceId;
                db.Add(cs);
            }
        }
    }
}
