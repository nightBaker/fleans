using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreMessageStartEventListenerGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreMessageStartEventListenerGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.MessageStartEventListeners.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

        if (state is not null)
        {
            var registrations = await db.MessageStartEventRegistrations.AsNoTracking()
                .Where(r => r.MessageName == id)
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
        var state = (MessageStartEventListenerState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.MessageStartEventListeners.FindAsync(id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.MessageStartEventListeners.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ETag = newETag;
        }

        // Diff registrations
        var existingRegs = await db.MessageStartEventRegistrations
            .Where(r => r.MessageName == id)
            .ToListAsync();

        var existingKeys = existingRegs.Select(r => r.ProcessDefinitionKey).ToHashSet();
        var newKeys = state.ProcessDefinitionKeys.ToHashSet();

        foreach (var reg in existingRegs.Where(r => !newKeys.Contains(r.ProcessDefinitionKey)))
            db.MessageStartEventRegistrations.Remove(reg);

        foreach (var key in newKeys.Where(k => !existingKeys.Contains(k)))
            db.MessageStartEventRegistrations.Add(new MessageStartEventRegistration(id, key));

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.MessageStartEventListeners.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            // Remove registrations first, then the parent
            var regs = await db.MessageStartEventRegistrations.Where(r => r.MessageName == id).ToListAsync();
            db.MessageStartEventRegistrations.RemoveRange(regs);
            db.MessageStartEventListeners.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
