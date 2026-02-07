using System.Dynamic;

namespace Fleans.Application.Conditions;

public interface IConditionExpressionEvaluatorGrain : IGrainWithIntegerKey
{
    Task<bool> Evaluate(string expression, ExpandoObject variables);
}
