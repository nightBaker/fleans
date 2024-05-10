namespace Fleans.Domain;

public class WorklfowVariablesState
{
    public Guid Id { get; private set; }

    public Dictionary<string, object> Variables { get; } = new();

    internal void Merge(Dictionary<string, object> variables)
    {
        throw new NotImplementedException();
    }
}