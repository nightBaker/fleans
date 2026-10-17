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

    /// <summary>
    /// The entries of <paramref name="variables"/> that <see cref="Merge"/> would change: keys this
    /// scope lacks, or holds a different value for (<see cref="VariableValues.AreEqual"/>). Merging
    /// the result leaves the same state as merging all of <paramref name="variables"/>.
    /// </summary>
    internal ExpandoObject ChangesFrom(ExpandoObject variables)
    {
        var current = (IDictionary<string, object?>)Variables;
        var changes = new ExpandoObject();
        var target = (IDictionary<string, object?>)changes;
        foreach (var (key, value) in (IDictionary<string, object?>)variables)
        {
            if (!current.TryGetValue(key, out var existing) || !VariableValues.AreEqual(existing, value))
                target[key] = value;
        }
        return changes;
    }
}
