namespace Fleans.Domain.States;

[GenerateSerializer]
public class SignalStartEventListenerState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<string> ProcessDefinitionKeys { get; set; } = [];

    public void AddProcess(string processDefinitionKey)
    {
        if (!ProcessDefinitionKeys.Contains(processDefinitionKey))
            ProcessDefinitionKeys.Add(processDefinitionKey);
    }

    public void RemoveProcess(string processDefinitionKey)
    {
        ProcessDefinitionKeys.Remove(processDefinitionKey);
    }

    public bool IsEmpty => ProcessDefinitionKeys.Count == 0;
}
