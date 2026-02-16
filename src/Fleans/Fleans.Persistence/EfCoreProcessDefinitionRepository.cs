using Fleans.Domain;
using Fleans.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class EfCoreProcessDefinitionRepository : IProcessDefinitionRepository
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreProcessDefinitionRepository(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.ProcessDefinitionId == processDefinitionId);
    }

    public async Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .AsNoTracking()
            .Where(d => d.ProcessDefinitionKey == processDefinitionKey)
            .OrderBy(d => d.Version)
            .ToListAsync();
    }

    public async Task<List<ProcessDefinition>> GetAllAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .AsNoTracking()
            .OrderBy(d => d.ProcessDefinitionKey)
            .ThenBy(d => d.Version)
            .ToListAsync();
    }

    public async Task SaveAsync(ProcessDefinition definition)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var existing = await db.ProcessDefinitions.FindAsync(definition.ProcessDefinitionId);
        if (existing is not null)
            throw new InvalidOperationException(
                $"Process definition '{definition.ProcessDefinitionId}' already exists.");

        definition.ETag ??= Guid.NewGuid().ToString("N");
        db.ProcessDefinitions.Add(definition);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string processDefinitionId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var existing = await db.ProcessDefinitions.FindAsync(processDefinitionId);
        if (existing is null)
            return;

        db.ProcessDefinitions.Remove(existing);
        await db.SaveChangesAsync();
    }
}
