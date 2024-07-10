using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Application.Conditions;

[StatelessWorker]
public class ConditionExpressionEvaluaterGrain : Grain, IConditionExpressionEvaluaterGrain
{
    private readonly IConditionExpressionEvaluater _conditionExpressionEvaluater;

    public ConditionExpressionEvaluaterGrain(IConditionExpressionEvaluater conditionExpressionEvaluater)
    {
        _conditionExpressionEvaluater = conditionExpressionEvaluater;
    }

    public Task<bool> Evaluate(string expression, ExpandoObject variables)
    {
        return _conditionExpressionEvaluater.Evaluate(expression, variables);
    }
}
