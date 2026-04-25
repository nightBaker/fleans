using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreConditionalStartEventRegistryGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreConditionalStartEventRegistryGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var entries = await db.ConditionalStartEventRegistryEntries.AsNoTracking().ToListAsync();

        var state = new ConditionalStartEventRegistryState
        {
            Entries = entries
        };

        if (entries.Count > 0)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = "loaded";
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var state = (ConditionalStartEventRegistryState)(object)grainState.State!;

        var existingEntries = await db.ConditionalStartEventRegistryEntries.ToListAsync();
        var existingKeys = existingEntries
            .Select(e => (e.ProcessDefinitionKey, e.ActivityId))
            .ToHashSet();
        var newKeys = state.Entries
            .Select(e => (e.ProcessDefinitionKey, e.ActivityId))
            .ToHashSet();

        foreach (var entry in existingEntries.Where(e => !newKeys.Contains((e.ProcessDefinitionKey, e.ActivityId))))
            db.ConditionalStartEventRegistryEntries.Remove(entry);

        foreach (var entry in state.Entries.Where(e => !existingKeys.Contains((e.ProcessDefinitionKey, e.ActivityId))))
            db.ConditionalStartEventRegistryEntries.Add(new ConditionalStartEntryState
            {
                ProcessDefinitionKey = entry.ProcessDefinitionKey,
                ActivityId = entry.ActivityId,
                ConditionExpression = entry.ConditionExpression
            });

        // Update condition expressions for existing entries that may have changed
        foreach (var entry in state.Entries.Where(e => existingKeys.Contains((e.ProcessDefinitionKey, e.ActivityId))))
        {
            var existing = existingEntries.First(e =>
                e.ProcessDefinitionKey == entry.ProcessDefinitionKey && e.ActivityId == entry.ActivityId);
            existing.ConditionExpression = entry.ConditionExpression;
        }

        await db.SaveChangesAsync();

        grainState.ETag = Guid.NewGuid().ToString("N");
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var entries = await db.ConditionalStartEventRegistryEntries.ToListAsync();
        db.ConditionalStartEventRegistryEntries.RemoveRange(entries);
        await db.SaveChangesAsync();

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
