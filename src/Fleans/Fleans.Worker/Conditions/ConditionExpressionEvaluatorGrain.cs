using System.Dynamic;
using Fleans.Application.Conditions;
using Orleans.Concurrency;

namespace Fleans.Worker.Conditions;

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
