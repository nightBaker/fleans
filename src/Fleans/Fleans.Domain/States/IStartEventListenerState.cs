namespace Fleans.Domain.States;

public interface IStartEventListenerState
{
    List<string> ProcessDefinitionKeys { get; }
    bool AddProcess(string processDefinitionKey);
    bool RemoveProcess(string processDefinitionKey);
    bool IsEmpty { get; }
}
