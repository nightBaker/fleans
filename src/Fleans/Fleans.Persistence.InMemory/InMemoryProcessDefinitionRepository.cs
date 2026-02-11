using Fleans.Domain;
using Fleans.Domain.Persistence;

namespace Fleans.Persistence.InMemory;

public class InMemoryProcessDefinitionRepository : IProcessDefinitionRepository
{
    private readonly Dictionary<string, ProcessDefinition> _store = new(StringComparer.Ordinal);

    public Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId)
    {
        _store.TryGetValue(processDefinitionId, out var definition);
        return Task.FromResult(definition);
    }

    public Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey)
    {
        var result = _store.Values
            .Where(d => string.Equals(d.ProcessDefinitionKey, processDefinitionKey, StringComparison.Ordinal))
            .OrderBy(d => d.Version)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<List<ProcessDefinition>> GetAllAsync()
    {
        var result = _store.Values
            .OrderBy(d => d.ProcessDefinitionKey, StringComparer.Ordinal)
            .ThenBy(d => d.Version)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(ProcessDefinition definition)
    {
        _store[definition.ProcessDefinitionId] = definition;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string processDefinitionId)
    {
        _store.Remove(processDefinitionId);
        return Task.CompletedTask;
    }
}
