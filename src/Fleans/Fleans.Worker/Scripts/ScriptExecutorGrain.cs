using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Application.Observability;
using Fleans.Application.Placement;
using Fleans.Application.Scripts;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Worker.Scripts;

// [StatelessWorker] is the placement strategy (many local activations); [WorkerPlacement] is a
// placement filter that keeps it on worker-/combined- silos. Callers spread keys via WorkerEvaluatorOptions.NextKey().
[StatelessWorker]
[WorkerPlacement]
public partial class ScriptExecutorGrain : Grain, IScriptExecutorGrain
{
    /// <summary>Tries of one result callback before giving up; the instance re-dispatches after that.</summary>
    internal const int CallbackAttempts = 5;
    private static readonly TimeSpan CallbackBaseDelay = TimeSpan.FromMilliseconds(200);

    private readonly IScriptExpressionExecutor _scriptExpressionExecutor;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ScriptExecutorGrain> _logger;
    private readonly string _siloName;

    public ScriptExecutorGrain(
        IScriptExpressionExecutor scriptExpressionExecutor,
        IGrainFactory grainFactory,
        ILogger<ScriptExecutorGrain> logger,
        ILocalSiloDetails siloDetails)
    {
        _scriptExpressionExecutor = scriptExpressionExecutor;
        _grainFactory = grainFactory;
        _logger = logger;
        _siloName = siloDetails.Name;
    }

    public async Task Run(ExecuteScriptEvent request, ExpandoObject variables)
    {
        // The business id arrives in the RequestContext of the instance's dispatch.
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, request.WorkflowId, request.ProcessDefinitionId, request.WorkflowInstanceId, request.ActivityId);
        LogScriptExecuting(_siloName, request.ScriptFormat, request.ActivityId);

        var instance = _grainFactory.GetGrain<IWorkflowInstanceCallback>(request.WorkflowInstanceId);

        ExpandoObject result;
        try
        {
            result = await _scriptExpressionExecutor.Execute(request.Script, variables, request.ScriptFormat);
        }
        catch (Exception ex)
        {
            LogScriptFailed(ex, request.ActivityId);
            await Fail(request, instance, ex);
            return;
        }

        try
        {
            await Report(request, nameof(IWorkflowInstanceCallback.CompleteActivity),
                () => instance.CompleteActivity(request.ActivityId, request.ActivityInstanceId, result));
        }
        // The instance rejected the result (e.g. variables over the size limit): fail the activity
        // instead, as it would be stuck otherwise. A transient failure was already retried.
        catch (Exception ex) when (!IsTransient(ex))
        {
            LogCompletionRejected(ex, request.ActivityId);
            await Fail(request, instance, ex);
        }
    }

    private async Task Fail(ExecuteScriptEvent request, IWorkflowInstanceCallback instance, Exception ex)
    {
        try
        {
            await Report(request, nameof(IWorkflowInstanceCallback.FailActivity),
                () => instance.FailActivity(request.ActivityId, request.ActivityInstanceId, ex));
        }
        catch (Exception failEx)
        {
            LogCallbackGaveUp(failEx, nameof(IWorkflowInstanceCallback.FailActivity), request.ActivityId, 1);
            FleansDiagnostics.OnWorkflowStalled("script");
        }
    }

    /// <summary>
    /// Calls the instance, retrying transient failures (a silo going away, a retiring activation —
    /// #845) with back-off: the instance drops a repeated result for a finished activity. After the
    /// last try the result is lost; the instance's dispatch timeout re-dispatches the script.
    /// </summary>
    private async Task Report(ExecuteScriptEvent request, string callbackName, Func<Task> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await call();
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt >= CallbackAttempts)
                {
                    LogCallbackGaveUp(ex, callbackName, request.ActivityId, attempt);
                    FleansDiagnostics.OnWorkflowStalled("script");
                    return;
                }
                LogCallbackRetried(ex, callbackName, request.ActivityId, attempt);
                await Task.Delay(CallbackBaseDelay * Math.Pow(2, attempt - 1));
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is TimeoutException or OrleansException;

    [LoggerMessage(EventId = 9020, Level = LogLevel.Debug,
        Message = "ScriptExecutorGrain executing on silo={silo} format={scriptFormat} activity={ActivityId}")]
    private partial void LogScriptExecuting(string silo, string scriptFormat, string activityId);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Error, Message = "Script execution failed for activity {ActivityId}")]
    private partial void LogScriptFailed(Exception ex, string activityId);

    [LoggerMessage(EventId = 9022, Level = LogLevel.Warning,
        Message = "Workflow instance rejected the script result for activity {ActivityId}; failing the activity")]
    private partial void LogCompletionRejected(Exception ex, string activityId);

    [LoggerMessage(EventId = 9023, Level = LogLevel.Warning,
        Message = "{Callback} for script activity {ActivityId} failed transiently (try {Attempt}); retrying")]
    private partial void LogCallbackRetried(Exception ex, string callback, string activityId, int attempt);

    [LoggerMessage(EventId = 9024, Level = LogLevel.Critical,
        Message = "{Callback} for script activity {ActivityId} failed {Attempt} times; the instance re-dispatches the script when its result timeout expires")]
    private partial void LogCallbackGaveUp(Exception ex, string callback, string activityId, int attempt);
}
