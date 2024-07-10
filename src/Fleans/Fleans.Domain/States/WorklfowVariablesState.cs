using System.Dynamic;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class WorklfowVariablesState
{
    [Id(0)]
    public Guid Id { get; private set; }

    [Id(1)]
    public ExpandoObject Variables { get; set; } = new();
    

    internal void Merge(ExpandoObject variables)
    {
        Variables = Combine(Variables, variables) ;
    }
    internal void CloneFrom(WorklfowVariablesState source)
    {
        Merge(source.Variables);
    }

    static ExpandoObject Combine(dynamic item1, dynamic item2)
    {
        var dictionary1 = (IDictionary<string, object>)item1;
        var dictionary2 = (IDictionary<string, object>)item2;
        var result = new ExpandoObject();
        var d = result as IDictionary<string, object>; //work with the Expando as a Dictionary

        foreach (var pair in dictionary1.Concat(dictionary2))
        {
            d[pair.Key] = pair.Value;
        }

        return result;
    }
}
