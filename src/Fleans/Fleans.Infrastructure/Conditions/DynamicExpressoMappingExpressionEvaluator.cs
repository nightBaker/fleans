using System.Collections.Concurrent;
using System.Dynamic;
using System.Globalization;
using System.Text.RegularExpressions;
using DynamicExpresso;
using Fleans.Domain;

namespace Fleans.Infrastructure.Conditions;

/// <summary>
/// Evaluates <c>fleans:ioMapping</c> sources. After the leading <c>=</c> comes a C# expression in
/// the DynamicExpresso dialect of gateway conditions; the scope's variables are top-level
/// identifiers (<c>=order.Total * 1.12m</c>) and the whole scope is also <c>_context</c>.
/// <list type="bullet">
///   <item><description>No leading <c>=</c>: the source is a string literal.</description></item>
///   <item><description>A plain path (<c>=a.b.c</c>) walks variables and nested objects without
///   compiling; a missing segment yields <c>null</c>.</description></item>
///   <item><description>An integer literal yields a <c>long</c>, like a JSON variable.</description></item>
/// </list>
/// </summary>
public partial class DynamicExpressoMappingExpressionEvaluator : IMappingExpressionEvaluator
{
    private const string ContextIdentifier = "_context";

    // The variables each expression names, and its compiled lambdas keyed by expression text plus
    // the types of those variables (a lambda is typed by its parameters). Lambdas are immutable and
    // thread-safe; expressions come from deployed definitions, so both caches stay small.
    private readonly ConcurrentDictionary<string, string[]> _identifiers = new();
    private readonly ConcurrentDictionary<string, Lambda> _lambdas = new();

    public object? Evaluate(string source, ExpandoObject variables)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variables);

        if (source.Length == 0 || source[0] != '=')
            return source;

        var expression = source[1..].Trim();
        if (expression.Length == 0)
            throw new InvalidOperationException("The expression after '=' is empty.");

        if (long.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;

        var scope = (IDictionary<string, object?>)variables;
        if (PathRegex().IsMatch(expression) && expression is not ("true" or "false" or "null"))
            return WalkPath(expression, scope);

        return Compile(expression, scope);
    }

    private object? Compile(string expression, IDictionary<string, object?> scope)
    {
        var identifiers = _identifiers.GetOrAdd(expression,
            expr => new Interpreter().DetectIdentifiers(expr).UnknownIdentifiers.ToArray());
        var values = identifiers.Select(name => ValueOf(name, scope)).ToArray();
        var types = values.Select(TypeOf).ToArray();

        var key = expression + "\0" + string.Join(",", types.Select(t => t.FullName));
        var lambda = _lambdas.GetOrAdd(key, _ => new Interpreter().Parse(expression,
            identifiers.Select((name, i) => new Parameter(name, types[i])).ToArray()));
        return lambda.Invoke(values);
    }

    // Dynamic objects bind members at run time; any other value compiles against its own type.
    private static Type TypeOf(object? value) =>
        value is IDynamicMetaObjectProvider ? typeof(ExpandoObject) : value?.GetType() ?? typeof(object);

    private static object? ValueOf(string identifier, IDictionary<string, object?> scope)
    {
        if (scope.TryGetValue(identifier, out var value))
            return value;
        return identifier == ContextIdentifier ? scope : null;
    }

    private static object? WalkPath(string path, IDictionary<string, object?> scope)
    {
        var segments = path.Split('.');
        object? current = scope;
        for (var i = 0; i < segments.Length; i++)
        {
            if (i == 0 && segments[0] == ContextIdentifier && !scope.ContainsKey(ContextIdentifier))
                continue;
            if (current is not IDictionary<string, object?> dict || !dict.TryGetValue(segments[i], out current))
                return null;
        }
        return current;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex PathRegex();
}
