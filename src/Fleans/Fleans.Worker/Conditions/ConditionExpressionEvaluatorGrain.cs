using System.Dynamic;
using Fleans.Application.Conditions;
using Fleans.Application.Placement;
using Orleans.Concurrency;

namespace Fleans.Worker.Conditions;

// [StatelessWorker] is the placement strategy (many local activations); [WorkerPlacement] is a
// placement filter that keeps it on worker-/combined- silos. Callers spread keys via WorkerEvaluatorOptions.NextKey().
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
