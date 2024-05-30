namespace Fleans.Domain.States;

public class WorklfowVariablesState
{
    public Guid Id { get; private set; }

    public Dictionary<string, object> Variables { get; } = new();

    internal void Merge(Dictionary<string, object> variables)
    {
        foreach (var key in variables.Keys)
        {
            if (Variables.ContainsKey(key))
            {
                Variables[key] = variables[key];
            }
            else
            {
                Variables.Add(key, variables[key]);
            }
        }
    }
    internal void CloneFrom(WorklfowVariablesState source)
    {
        Merge(source.Variables);
    }
}
