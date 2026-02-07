using System.Dynamic;
using DynamicExpresso;
using Fleans.Application.Scripts;

namespace Fleans.Infrastructure.Scripts;

public class DynamicExpressoScriptExpressionExecutor : IScriptExpressionExecutor
{
    public Task<ExpandoObject> Execute(string script, ExpandoObject variables)
    {
        var interpreter = new Interpreter().SetVariable("_context", variables);

        var statements = script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var statement in statements)
        {
            interpreter.Eval(statement);
        }

        return Task.FromResult(variables);
    }
}
