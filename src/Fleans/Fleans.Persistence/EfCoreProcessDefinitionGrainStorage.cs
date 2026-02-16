using Fleans.Domain;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreProcessDefinitionGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreProcessDefinitionGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.ProcessDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ProcessDefinitionId == id);

        if (state is not null)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (ProcessDefinition)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.ProcessDefinitions.FindAsync(id);

        if (existing is null)
        {
            if (grainState.ETag is not null)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', but no record exists");

            db.ProcessDefinitions.Add(state);
            db.Entry(state).Property(s => s.ProcessDefinitionId).CurrentValue = id;
            db.Entry(state).Property(s => s.ETag).CurrentValue = newETag;
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.ProcessDefinitionId).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;
        }

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.ProcessDefinitions.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.ProcessDefinitions.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
