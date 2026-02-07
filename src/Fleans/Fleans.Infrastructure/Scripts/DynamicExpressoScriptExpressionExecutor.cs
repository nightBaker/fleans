using System.Dynamic;
using DynamicExpresso;
using Fleans.Application.Scripts;

namespace Fleans.Infrastructure.Scripts;

public class DynamicExpressoScriptExpressionExecutor : IScriptExpressionExecutor
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "c#", ""
    };

    public async Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat)
    {
        if (!SupportedFormats.Contains(scriptFormat))
            throw new NotSupportedException($"Script format '{scriptFormat}' is not supported. Supported formats: csharp.");

        if (string.IsNullOrWhiteSpace(script))
            return variables;

        var task = Task.Run(() =>
        {
            var interpreter = new Interpreter().SetVariable("_context", variables);

            foreach (var statement in SplitStatements(script))
            {
                interpreter.Eval(statement);
            }
        });

        await task.WaitAsync(ScriptTimeout);

        return variables;
    }

    public static IEnumerable<string> SplitStatements(string script)
    {
        var current = 0;
        var start = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        while (current < script.Length)
        {
            var c = script[current];

            if (c == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;
            else if (c == '\\' && (inSingleQuote || inDoubleQuote))
            {
                current++; // skip escaped character
            }
            else if (c == ';' && !inSingleQuote && !inDoubleQuote)
            {
                var statement = script[start..current].Trim();
                if (statement.Length > 0)
                    yield return statement;
                start = current + 1;
            }

            current++;
        }

        var last = script[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }
}
