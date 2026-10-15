using Fleans.Application;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// Business id uniqueness lookup (#824). Deliberately uses <see cref="FleanCommandDbContext"/>
/// (the primary) rather than the query context, which may point at a lagging read replica.
/// </summary>
public class EfCoreBusinessIdLookup : IBusinessIdLookup
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreBusinessIdLookup(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<Guid?> FindActiveRootInstance(string processDefinitionKey, string businessId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionIds = db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .Select(p => p.ProcessDefinitionId);

        return await db.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.BusinessId == businessId
                && w.IsStarted
                && !w.IsCompleted
                && w.ParentWorkflowInstanceId == null
                && definitionIds.Contains(w.ProcessDefinitionId))
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync();
    }
}
