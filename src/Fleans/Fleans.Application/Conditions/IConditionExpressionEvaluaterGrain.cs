using System.Dynamic;

namespace Fleans.Application.Conditions;

public interface IConditionExpressionEvaluaterGrain : IGrainWithIntegerKey
{
    Task<bool> Evaluate(string expression, ExpandoObject variables);
}
