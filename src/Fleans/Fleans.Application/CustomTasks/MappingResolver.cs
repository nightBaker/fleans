using System.Dynamic;
using Fleans.Domain.Errors;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Resolves an input/output mapping <c>source</c> string against a dictionary-shaped scope.
///
/// Source grammar (validated at deploy time by BpmnConverter):
/// <list type="bullet">
///   <item><description><c>=identifier</c> — top-level lookup, returns <c>scope[identifier]</c></description></item>
///   <item><description><c>=identifier.path.to.field</c> — dot-walk through nested dictionaries / ExpandoObjects</description></item>
///   <item><description><c>=&quot;literal&quot;</c> — quoted string literal</description></item>
///   <item><description><c>=42</c> / <c>=true</c> / <c>=false</c> / <c>=null</c> — primitive literal</description></item>
///   <item><description><c>bare-string</c> (no leading <c>=</c>) — string literal</description></item>
/// </list>
/// </summary>
public static class MappingResolver
{
    public static object? Resolve(string source, IDictionary<string, object?> scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);

        if (source.Length == 0 || source[0] != '=')
            return source; // bare string literal

        var expr = source.Substring(1);

        if (expr.Length == 0)
            throw new CustomTaskFailedActivityException(400, "Mapping source '=' is empty after the leading '='.");

        // Quoted string: ="..."
        if (expr[0] == '"')
        {
            if (expr.Length < 2 || expr[expr.Length - 1] != '"')
                throw new CustomTaskFailedActivityException(400, $"Mapping source has unmatched quote: '{source}'");
            return expr.Substring(1, expr.Length - 2);
        }

        // Primitive literals
        if (expr == "true") return true;
        if (expr == "false") return false;
        if (expr == "null") return null;
        if (long.TryParse(expr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longVal))
            return longVal;
        if (double.TryParse(expr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleVal))
            return doubleVal;

        // Path expression: identifier(.identifier)*
        return WalkPath(expr, scope, source);
    }

    private static object? WalkPath(string expr, IDictionary<string, object?> scope, string fullSource)
    {
        var segments = expr.Split('.');
        object? current = scope;

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new CustomTaskFailedActivityException(400, $"Mapping source has empty path segment: '{fullSource}'");

            if (current is null)
                return null;

            if (current is IDictionary<string, object?> dict)
            {
                if (!dict.TryGetValue(segment, out current))
                    return null;
            }
            else if (current is ExpandoObject expando)
            {
                var expandoDict = (IDictionary<string, object?>)expando;
                if (!expandoDict.TryGetValue(segment, out current))
                    return null;
            }
            else
            {
                return null;
            }
        }

        return current;
    }
}
