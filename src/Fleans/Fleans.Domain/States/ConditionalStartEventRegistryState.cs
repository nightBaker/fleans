namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionalStartEventRegistryState
{
    [Id(0)] public string? ETag { get; set; }
    [Id(1)] public List<ConditionalStartEntryState> Entries { get; set; } = [];

    public bool Add(string processDefinitionKey, string activityId, string conditionExpression)
    {
        if (Entries.Any(e => e.ProcessDefinitionKey == processDefinitionKey && e.ActivityId == activityId))
            return false;

        Entries.Add(new ConditionalStartEntryState
        {
            ProcessDefinitionKey = processDefinitionKey,
            ActivityId = activityId,
            ConditionExpression = conditionExpression
        });
        return true;
    }

    public bool Remove(string processDefinitionKey, string activityId)
    {
        return Entries.RemoveAll(e =>
            e.ProcessDefinitionKey == processDefinitionKey && e.ActivityId == activityId) > 0;
    }

    public int RemoveAllForProcess(string processDefinitionKey)
    {
        return Entries.RemoveAll(e => e.ProcessDefinitionKey == processDefinitionKey);
    }
}

[GenerateSerializer]
public class ConditionalStartEntryState
{
    [Id(0)] public string ProcessDefinitionKey { get; set; } = string.Empty;
    [Id(1)] public string ActivityId { get; set; } = string.Empty;
    [Id(2)] public string ConditionExpression { get; set; } = string.Empty;
}
