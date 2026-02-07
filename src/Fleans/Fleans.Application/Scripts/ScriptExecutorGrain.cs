using System.Dynamic;
using Orleans.Concurrency;

namespace Fleans.Application.Scripts;

[StatelessWorker]
public class ScriptExecutorGrain : Grain, IScriptExecutorGrain
{
    private readonly IScriptExpressionExecutor _scriptExpressionExecutor;

    public ScriptExecutorGrain(IScriptExpressionExecutor scriptExpressionExecutor)
    {
        _scriptExpressionExecutor = scriptExpressionExecutor;
    }

    public Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat)
    {
        return _scriptExpressionExecutor.Execute(script, variables, scriptFormat);
    }
}
