using System.Collections.Concurrent;
using System.Dynamic;
using DynamicExpresso;
using Fleans.Application.Conditions;

namespace Fleans.Infrastructure.Conditions;

public class DynamicExpressoConditionExpressionEvaluator : IConditionExpressionEvaluator
{
    private readonly ConcurrentDictionary<string, Lambda> _cache = new();

    public Task<bool> Evaluate(string expression, ExpandoObject variables)
    {
        var lambda = _cache.GetOrAdd(expression, expr =>
        {
            var interpreter = new Interpreter();
            return interpreter.Parse(expr, new Parameter("_context", typeof(ExpandoObject)));
        });

        var result = (bool)lambda.Invoke(variables);
        return Task.FromResult(result);
    }
}
