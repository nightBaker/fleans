using DynamicExpresso;
using Fleans.Application.Conditions;
using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;
using System.Dynamic;

namespace Fleans.Infrastructure.EventHandlers;

public class DynamicExperessoConditionExpressionEvaluater : IConditionExpressionEvaluater
{
    public Task<bool> Evaluate(string expression, ExpandoObject variables)
    {
        var interpreter = new Interpreter().SetVariable("_context", variables);
        var result = interpreter.Eval<bool>(expression);

        return Task.FromResult(result);
    }
}
