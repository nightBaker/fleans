using System.Dynamic;
using Fleans.Application.Conditions;
using Fleans.Worker.Placement;
using Orleans.Concurrency;

namespace Fleans.Worker.Conditions;

[StatelessWorker]
[WorkerPlacement]
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
