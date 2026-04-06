using Fleans.Domain;
using Fleans.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class EfCoreProcessDefinitionRepository : IProcessDefinitionRepository
{
    private readonly IDbContextFactory<FleanCommandDbContext> _commandDbFactory;
    private readonly IDbContextFactory<FleanQueryDbContext> _queryDbFactory;

    public EfCoreProcessDefinitionRepository(
        IDbContextFactory<FleanCommandDbContext> commandDbFactory,
        IDbContextFactory<FleanQueryDbContext> queryDbFactory)
    {
        _commandDbFactory = commandDbFactory;
        _queryDbFactory = queryDbFactory;
    }

    public async Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId)
    {
        await using var db = await _queryDbFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .FirstOrDefaultAsync(d => d.ProcessDefinitionId == processDefinitionId);
    }

    public async Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey)
    {
        await using var db = await _queryDbFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .Where(d => d.ProcessDefinitionKey == processDefinitionKey)
            .OrderBy(d => d.Version)
            .ToListAsync();
    }

    public async Task<List<ProcessDefinition>> GetAllAsync()
    {
        await using var db = await _queryDbFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .OrderBy(d => d.ProcessDefinitionKey)
            .ThenBy(d => d.Version)
            .ToListAsync();
    }

    public async Task<List<string>> GetAllDistinctKeysAsync()
    {
        await using var db = await _queryDbFactory.CreateDbContextAsync();
        return await db.ProcessDefinitions
            .Select(d => d.ProcessDefinitionKey)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync();
    }

    public async Task SaveAsync(ProcessDefinition definition)
    {
        await using var db = await _commandDbFactory.CreateDbContextAsync();

        var existing = await db.ProcessDefinitions.FindAsync(definition.ProcessDefinitionId);
        if (existing is not null)
            throw new InvalidOperationException(
                $"Process definition '{definition.ProcessDefinitionId}' already exists.");

        definition.ETag ??= Guid.NewGuid().ToString("N");
        db.ProcessDefinitions.Add(definition);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Updates only the IsActive state of an existing process definition.
    /// Intentionally scoped to IsActive — other fields are immutable after deployment.
    /// </summary>
    public async Task UpdateAsync(ProcessDefinition definition)
    {
        await using var db = await _commandDbFactory.CreateDbContextAsync();

        var existing = await db.ProcessDefinitions.FindAsync(definition.ProcessDefinitionId)
            ?? throw new InvalidOperationException(
                $"Process definition '{definition.ProcessDefinitionId}' not found.");

        if (definition.IsActive)
            existing.Enable();
        else
            existing.Disable();

        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string processDefinitionId)
    {
        await using var db = await _commandDbFactory.CreateDbContextAsync();

        var existing = await db.ProcessDefinitions.FindAsync(processDefinitionId);
        if (existing is null)
            return;

        db.ProcessDefinitions.Remove(existing);
        await db.SaveChangesAsync();
    }
}
