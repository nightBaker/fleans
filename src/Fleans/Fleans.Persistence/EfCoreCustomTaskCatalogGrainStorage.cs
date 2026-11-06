using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

/// <summary>
/// EF-backed grain storage for the custom-task catalog (sub-issue A2 of #357).
/// Mirrors <see cref="EfCoreConditionalStartEventRegistryGrainStorage"/> — diff
/// against the existing rows on every WriteStateAsync; full-table read on
/// ReadStateAsync; truncate on ClearStateAsync. The same goes for the private-access
/// rows, which outlive the catalog rows of the hosts that declared them.
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
        var access = await db.CustomTaskPrivateAccess.AsNoTracking().ToListAsync();

        var state = new CustomTaskCatalogState { Entries = rows, PrivateAccess = access };
        var exists = rows.Count > 0 || access.Count > 0;
        grainState.State = (T)(object)state;
        grainState.ETag = exists ? "loaded" : null;
        grainState.RecordExists = exists;
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
                Category = entry.Category,
                IsPrivate = entry.IsPrivate,
            });

        // Update mutable fields on rows still present.
        foreach (var entry in state.Entries.Where(e => existingKeys.Contains((e.TaskType, e.SiloName))))
        {
            var existing = existingRows.First(e =>
                e.TaskType == entry.TaskType && e.SiloName == entry.SiloName);
            existing.DisplayName = entry.DisplayName;
            existing.ParameterSchemaJson = entry.ParameterSchemaJson;
            existing.Category = entry.Category;
            existing.IsPrivate = entry.IsPrivate;
        }

        var existingAccess = await db.CustomTaskPrivateAccess.ToListAsync();
        db.CustomTaskPrivateAccess.RemoveRange(
            existingAccess.Where(row => state.PrivateAccess.All(a => a.TaskType != row.TaskType)));
        foreach (var access in state.PrivateAccess)
        {
            var row = existingAccess.FirstOrDefault(e => e.TaskType == access.TaskType);
            if (row is null)
            {
                db.CustomTaskPrivateAccess.Add(new CustomTaskPrivateAccessState
                {
                    TaskType = access.TaskType,
                    AllowedProcessKeys = [.. access.AllowedProcessKeys],
                    AllowListManaged = access.AllowListManaged,
                });
                continue;
            }
            row.AllowedProcessKeys = [.. access.AllowedProcessKeys];
            row.AllowListManaged = access.AllowListManaged;
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
        db.CustomTaskPrivateAccess.RemoveRange(await db.CustomTaskPrivateAccess.ToListAsync());
        await db.SaveChangesAsync();

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
