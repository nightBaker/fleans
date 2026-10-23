namespace Fleans.Domain.Persistence;

public interface IProcessDefinitionRepository
{
    Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId);
    /// <summary>
    /// All versions of <paramref name="processDefinitionKey"/>, oldest first, without their BPMN
    /// XML (<see cref="ProcessDefinition.BpmnXml"/> is empty): the process-definition grain never
    /// reads it, and the editor reads it through the query service.
    /// </summary>
    Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey);
    Task<List<ProcessDefinition>> GetAllAsync();
    Task SaveAsync(ProcessDefinition definition);
    Task UpdateAsync(ProcessDefinition definition);
    Task DeleteAsync(string processDefinitionId);
}
