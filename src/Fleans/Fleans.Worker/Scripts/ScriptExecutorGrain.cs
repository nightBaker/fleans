using System.Dynamic;
using Fleans.Application.Scripts;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Worker.Scripts;

[StatelessWorker]
[WorkerPlacement]
public partial class ScriptExecutorGrain : Grain, IScriptExecutorGrain
{
    private readonly IScriptExpressionExecutor _scriptExpressionExecutor;
    private readonly ILogger<ScriptExecutorGrain> _logger;
    private readonly string _siloName;

    public ScriptExecutorGrain(
        IScriptExpressionExecutor scriptExpressionExecutor,
        ILogger<ScriptExecutorGrain> logger,
        ILocalSiloDetails siloDetails)
    {
        _scriptExpressionExecutor = scriptExpressionExecutor;
        _logger = logger;
        _siloName = siloDetails.Name;
    }

    public Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat)
    {
        LogScriptExecuting(_siloName, scriptFormat);
        return _scriptExpressionExecutor.Execute(script, variables, scriptFormat);
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Debug,
        Message = "ScriptExecutorGrain executing on silo={silo} format={scriptFormat}")]
    private partial void LogScriptExecuting(string silo, string scriptFormat);
}
