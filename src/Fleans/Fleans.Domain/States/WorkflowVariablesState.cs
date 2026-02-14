using System.Dynamic;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class WorkflowVariablesState
{
    public WorkflowVariablesState(Guid id, Guid workflowInstanceId)
    {
        Id = id;
        WorkflowInstanceId = workflowInstanceId;
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

    internal void Merge(ExpandoObject variables)
    {
        Variables = Combine(Variables, variables);
    }

    static ExpandoObject Combine(dynamic item1, dynamic item2)
    {
        var dictionary1 = (IDictionary<string, object>)item1;
        var dictionary2 = (IDictionary<string, object>)item2;
        var result = new ExpandoObject();
        var d = result as IDictionary<string, object>;

        foreach (var pair in dictionary1.Concat(dictionary2))
        {
            d[pair.Key] = pair.Value;
        }

        return result;
    }
}
