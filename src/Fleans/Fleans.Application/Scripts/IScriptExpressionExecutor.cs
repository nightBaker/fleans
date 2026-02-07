using System.Dynamic;

namespace Fleans.Application.Scripts;

public interface IScriptExpressionExecutor
{
    Task<ExpandoObject> Execute(string script, ExpandoObject variables);
}
