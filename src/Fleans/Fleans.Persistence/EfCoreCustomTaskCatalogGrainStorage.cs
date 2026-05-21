using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

/// <summary>
/// EF-backed grain storage for the custom-task catalog (sub-issue A2 of #357).
/// Mirrors <see cref="EfCoreConditionalStartEventRegistryGrainStorage"/> — diff
/// against the existing rows on every WriteStateAsync; full-table read on
/// ReadStateAsync; truncate on ClearStateAsync.
/// </summary>
public class EfCoreCustomTaskCatalogGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreCustomTaskCatalogGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var rows = await db.CustomTaskCatalogEntries.AsNoTracking().ToListAsync();

        var state = new CustomTaskCatalogState { Entries = rows };
        grainState.State = (T)(object)state;
        grainState.ETag = rows.Count > 0 ? "loaded" : null;
        grainState.RecordExists = rows.Count > 0;
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var state = (CustomTaskCatalogState)(object)grainState.State!;

        var existingRows = await db.CustomTaskCatalogEntries.ToListAsync();
        var existingKeys = existingRows
            .Select(e => (e.TaskType, e.SiloName))
            .ToHashSet();
        var newKeys = state.Entries
            .Select(e => (e.TaskType, e.SiloName))
            .ToHashSet();

        // Remove rows the grain no longer holds.
        foreach (var row in existingRows.Where(e => !newKeys.Contains((e.TaskType, e.SiloName))))
            db.CustomTaskCatalogEntries.Remove(row);

        // Add rows the grain holds that aren't yet in the table.
        foreach (var entry in state.Entries.Where(e => !existingKeys.Contains((e.TaskType, e.SiloName))))
            db.CustomTaskCatalogEntries.Add(new CustomTaskCatalogRowState
            {
                TaskType = entry.TaskType,
                SiloName = entry.SiloName,
                DisplayName = entry.DisplayName,
                ParameterSchemaJson = entry.ParameterSchemaJson,
            });

        // Update mutable fields on rows still present.
        foreach (var entry in state.Entries.Where(e => existingKeys.Contains((e.TaskType, e.SiloName))))
        {
            var existing = existingRows.First(e =>
                e.TaskType == entry.TaskType && e.SiloName == entry.SiloName);
            existing.DisplayName = entry.DisplayName;
            existing.ParameterSchemaJson = entry.ParameterSchemaJson;
        }

        await db.SaveChangesAsync();

        grainState.ETag = Guid.NewGuid().ToString("N");
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var rows = await db.CustomTaskCatalogEntries.ToListAsync();
        db.CustomTaskCatalogEntries.RemoveRange(rows);
        await db.SaveChangesAsync();

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
