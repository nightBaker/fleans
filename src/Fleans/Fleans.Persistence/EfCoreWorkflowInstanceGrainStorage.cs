using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreWorkflowInstanceGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<GrainStateDbContext> _dbContextFactory;

    public EfCoreWorkflowInstanceGrainStorage(IDbContextFactory<GrainStateDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();

        var state = await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .Include(s => s.ConditionSequenceStates)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

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
        var id = grainId.GetGuidKey();
        var state = (WorkflowInstanceState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.WorkflowInstances.FindAsync(id);

        if (existing is null)
        {
            if (grainState.ETag is not null)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', but no record exists");
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.WorkflowInstances.Remove(existing);
            await db.SaveChangesAsync();
        }

        db.WorkflowInstances.Add(state);
        db.Entry(state).Property(s => s.Id).CurrentValue = id;
        db.Entry(state).Property(s => s.ETag).CurrentValue = newETag;
        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.GetGuidKey();
        var existing = await db.WorkflowInstances.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.WorkflowInstances.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
