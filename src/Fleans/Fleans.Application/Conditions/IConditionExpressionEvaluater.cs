using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Application.Conditions;

public interface IConditionExpressionEvaluater
{
    Task<bool> Evaluate(string expression, ExpandoObject variables);
}
