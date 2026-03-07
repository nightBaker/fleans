using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreSignalStartEventListenerGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreSignalStartEventListenerGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.SignalStartEventListeners.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

        if (state is not null)
        {
            var registrations = await db.StartEventRegistrations.AsNoTracking()
                .Where(r => r.EventName == id)
                .ToListAsync();

            state.ProcessDefinitionKeys = registrations.Select(r => r.ProcessDefinitionKey).ToList();

            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (SignalStartEventListenerState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.SignalStartEventListeners.FindAsync(id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.SignalStartEventListeners.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ETag = newETag;
        }

        // Diff registrations
        var existingRegs = await db.StartEventRegistrations
            .Where(r => r.EventName == id)
            .ToListAsync();

        var existingKeys = existingRegs.Select(r => r.ProcessDefinitionKey).ToHashSet();
        var newKeys = state.ProcessDefinitionKeys.ToHashSet();

        foreach (var reg in existingRegs.Where(r => !newKeys.Contains(r.ProcessDefinitionKey)))
            db.StartEventRegistrations.Remove(reg);

        foreach (var key in newKeys.Where(k => !existingKeys.Contains(k)))
            db.StartEventRegistrations.Add(new StartEventRegistration(id, key));

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.SignalStartEventListeners.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            // Remove registrations first, then the parent
            var regs = await db.StartEventRegistrations.Where(r => r.EventName == id).ToListAsync();
            db.StartEventRegistrations.RemoveRange(regs);
            db.SignalStartEventListeners.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
