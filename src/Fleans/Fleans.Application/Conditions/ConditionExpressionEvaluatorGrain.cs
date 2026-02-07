using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Application.Conditions;

[StatelessWorker]
public class ConditionExpressionEvaluatorGrain : Grain, IConditionExpressionEvaluatorGrain
{
    private readonly IConditionExpressionEvaluator _conditionExpressionEvaluator;

    public ConditionExpressionEvaluatorGrain(IConditionExpressionEvaluator conditionExpressionEvaluator)
    {
        _conditionExpressionEvaluator = conditionExpressionEvaluator;
    }

    public Task<bool> Evaluate(string expression, ExpandoObject variables)
    {
        return _conditionExpressionEvaluator.Evaluate(expression, variables);
    }
}
