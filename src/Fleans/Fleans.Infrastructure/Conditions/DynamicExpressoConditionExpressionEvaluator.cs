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
        var lambda = _cache.GetOrAdd(expression, Parse);

        var result = (bool)lambda.Invoke(variables);
        return Task.FromResult(result);
    }

    public void Compile(string expression)
    {
        var lambda = _cache.GetOrAdd(expression, Parse);

        // Member access on _context is dynamic and yields object; only a statically typed
        // non-boolean result (e.g. "1 + 2") is known to fail the cast in Evaluate.
        if (lambda.ReturnType != typeof(bool) && lambda.ReturnType != typeof(bool?) && lambda.ReturnType != typeof(object))
            throw new InvalidOperationException($"The condition yields {lambda.ReturnType.Name}, not a boolean.");
    }

    private static Lambda Parse(string expression) =>
        new Interpreter().Parse(expression, new Parameter("_context", typeof(ExpandoObject)));
}
