using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreEnvironmentVariablesGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreEnvironmentVariablesGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = await db.EnvironmentVariables
            .Include(e => e.Variables)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);
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
        var state = (EnvironmentVariablesState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.EnvironmentVariables
            .Include(e => e.Variables)
            .FirstOrDefaultAsync(e => e.Key == id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.EnvironmentVariables.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Key).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;

            DiffVariables(db, existing, state);
        }

        await db.SaveChangesAsync();
        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.EnvironmentVariables.FindAsync(id);
        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");
            db.EnvironmentVariables.Remove(existing);
            await db.SaveChangesAsync();
        }
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private static void DiffVariables(
        FleanCommandDbContext db,
        EnvironmentVariablesState existing,
        EnvironmentVariablesState state)
    {
        var existingById = existing.Variables.ToDictionary(v => v.Id);
        var newIds = state.Variables.Select(v => v.Id).ToHashSet();

        foreach (var v in existing.Variables.Where(v => !newIds.Contains(v.Id)).ToList())
            db.EnvironmentVariableEntries.Remove(v);

        foreach (var v in state.Variables)
        {
            if (existingById.TryGetValue(v.Id, out var existingVar))
            {
                db.Entry(existingVar).CurrentValues.SetValues(v);
                db.Entry(existingVar).Property(e => e.Id).IsModified = false;
            }
            else
            {
                existing.Variables.Add(v);
            }
        }
    }
}
