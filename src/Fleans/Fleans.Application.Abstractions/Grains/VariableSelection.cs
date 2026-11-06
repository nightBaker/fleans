using System.Dynamic;

namespace Fleans.Application.Grains;

/// <summary>
/// Which of a task's visible variables to read: all of them, or the top-level ones in
/// <see cref="Names"/> (none when it is empty). A name no visible variable has is left out.
/// </summary>
[GenerateSerializer]
public sealed record VariableSelection([property: Id(0)] bool IncludeAll, [property: Id(1)] List<string> Names)
{
    public static VariableSelection All => new(true, []);

    public static VariableSelection None => new(false, []);

    /// <summary>What covers every one of <paramref name="selections"/>; <see cref="None"/> when there are none.</summary>
    public static VariableSelection Union(IEnumerable<VariableSelection> selections)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (selection.IncludeAll)
                return All;
            names.UnionWith(selection.Names);
        }
        return new VariableSelection(false, names.ToList());
    }

    public ExpandoObject Apply(ExpandoObject variables)
    {
        if (IncludeAll)
            return variables;

        var source = (IDictionary<string, object?>)variables;
        var selected = new ExpandoObject();
        var target = (IDictionary<string, object?>)selected;
        foreach (var name in Names)
        {
            if (source.TryGetValue(name, out var value))
                target[name] = value;
        }
        return selected;
    }
}
