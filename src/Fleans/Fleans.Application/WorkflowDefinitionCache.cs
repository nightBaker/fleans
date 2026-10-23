using System.Collections.Concurrent;
using Fleans.Application.Grains;
using Fleans.Domain;
using Microsoft.Extensions.Logging;

namespace Fleans.Application;

/// <summary>
/// One deployed process definition per silo (#860). A deployed version never changes, so the
/// definition a <see cref="IProcessDefinitionGrain"/> returns for a <c>ProcessDefinitionId</c> is
/// kept for the life of the silo and shared by every <c>WorkflowInstance</c> activation of that
/// version: an activation reads it from here instead of calling the grain and holding its own copy.
/// </summary>
/// <remarks>
/// Concurrent misses for the same id share one grain call (the entry is the load's task); a failed
/// load is dropped so the next caller retries. Entries are never evicted: the cache holds the
/// versions that have instances on this silo, which a redeploy-heavy setup grows until restart.
/// </remarks>
public sealed partial class WorkflowDefinitionCache
{
    private readonly ConcurrentDictionary<string, Task<IWorkflowDefinition>> _definitions = new(StringComparer.Ordinal);
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowDefinitionCache> _logger;

    public WorkflowDefinitionCache(IGrainFactory grainFactory, ILogger<WorkflowDefinitionCache> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the definition of <paramref name="processDefinitionId"/>, loading it from its
    /// <see cref="IProcessDefinitionGrain"/> on the first call in this silo. Throws
    /// <see cref="KeyNotFoundException"/> when no such version is deployed.
    /// </summary>
    public async ValueTask<IWorkflowDefinition> GetAsync(string processDefinitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        var load = _definitions.GetOrAdd(processDefinitionId, Load);
        try
        {
            return await load;
        }
        catch
        {
            // Only this failed load: a retry that already replaced it stays.
            _definitions.TryRemove(KeyValuePair.Create(processDefinitionId, load));
            throw;
        }
    }

    private async Task<IWorkflowDefinition> Load(string processDefinitionId)
    {
        var key = ProcessDefinition.ExtractKeyFromId(processDefinitionId);
        IWorkflowDefinition definition = await _grainFactory.GetGrain<IProcessDefinitionGrain>(key)
            .GetDefinitionById(processDefinitionId);
        LogDefinitionLoaded(processDefinitionId);
        return definition;
    }

    [LoggerMessage(EventId = 6050, Level = LogLevel.Debug,
        Message = "Process definition {ProcessDefinitionId} loaded into the silo cache")]
    private partial void LogDefinitionLoaded(string processDefinitionId);
}
