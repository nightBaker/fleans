using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Application.Conditions;

public interface IConditionExpressionEvaluator
{
    Task<bool> Evaluate(string expression, ExpandoObject variables);

    /// <summary>
    /// Compiles <paramref name="expression"/> without evaluating it and keeps it for later
    /// <see cref="Evaluate"/> calls. Throws when it does not compile or cannot yield a boolean;
    /// deployment calls this so a broken condition rejects the deploy (#975).
    /// </summary>
    void Compile(string expression);
}
