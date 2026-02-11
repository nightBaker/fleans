namespace Fleans.Domain.Persistence;

public interface IProcessDefinitionRepository
{
    Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId);
    Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey);
    Task<List<ProcessDefinition>> GetAllAsync();
    Task SaveAsync(ProcessDefinition definition);
    Task DeleteAsync(string processDefinitionId);
}
