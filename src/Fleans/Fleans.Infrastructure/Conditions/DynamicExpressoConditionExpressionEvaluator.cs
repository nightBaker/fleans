using System.Dynamic;
using DynamicExpresso;
using Fleans.Application.Conditions;

namespace Fleans.Infrastructure.Conditions;

public class DynamicExpressoConditionExpressionEvaluator : IConditionExpressionEvaluator
{
    public Task<bool> Evaluate(string expression, ExpandoObject variables)
    {
        var interpreter = new Interpreter().SetVariable("_context", variables);
        var result = interpreter.Eval<bool>(expression);

        return Task.FromResult(result);
    }
}
