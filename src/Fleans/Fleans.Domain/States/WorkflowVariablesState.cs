using System.Dynamic;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class WorkflowVariablesState
{
    public WorkflowVariablesState(Guid id, Guid workflowInstanceId, Guid? parentVariablesId = null)
    {
        Id = id;
        WorkflowInstanceId = workflowInstanceId;
        ParentVariablesId = parentVariablesId;
    }

    private WorkflowVariablesState()
    {
    }

    [Id(0)]
    public Guid Id { get; private set; }

    [Id(1)]
    public Guid WorkflowInstanceId { get; private set; }

    [Id(2)]
    public ExpandoObject Variables { get; private set; } = new();

    [Id(3)]
    public Guid? ParentVariablesId { get; private set; }

    internal void Merge(ExpandoObject variables)
    {
        var target = (IDictionary<string, object>)Variables;
        foreach (var kvp in (IDictionary<string, object>)variables)
        {
            target[kvp.Key] = kvp.Value;
        }
    }
}
