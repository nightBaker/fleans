using System.Dynamic;
using Fleans.Application.Scripts;

namespace Fleans.Infrastructure.Scripts;

/// <summary>
/// Routes a script to the registered <see cref="IScriptEngine"/> for its <c>scriptFormat</c>.
/// C# (DynamicExpresso) and JavaScript (Jint) are registered by <c>AddInfrastructure</c>; another
/// language registers its own <see cref="IScriptEngine"/> there with <c>TryAddEnumerable</c>.
/// </summary>
public sealed class ScriptExpressionExecutor : IScriptExpressionExecutor
{
    private readonly Dictionary<string, IScriptEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    public ScriptExpressionExecutor(IEnumerable<IScriptEngine> engines)
    {
        foreach (var engine in engines)
        {
            foreach (var format in engine.Formats)
            {
                if (!_engines.TryAdd(format, engine))
                    throw new InvalidOperationException(
                        $"Script format '{format}' is registered by both {_engines[format].GetType().Name} and {engine.GetType().Name}.");
            }
        }
    }

    public Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat) =>
        Resolve(scriptFormat).Execute(script, variables);

    public void Compile(string script, string scriptFormat) => Resolve(scriptFormat).Compile(script);

    private IScriptEngine Resolve(string scriptFormat)
    {
        // A missing format is the default, C#.
        if (_engines.TryGetValue(scriptFormat ?? "", out var engine))
            return engine;

        var supported = string.Join(", ", _engines.Keys.Where(f => f.Length > 0).Order(StringComparer.OrdinalIgnoreCase));
        throw new NotSupportedException($"Script format '{scriptFormat}' is not supported. Supported formats: {supported}.");
    }
}
