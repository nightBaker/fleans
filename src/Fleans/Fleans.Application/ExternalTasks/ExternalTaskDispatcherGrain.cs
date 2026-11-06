using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Application.Observability;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.ExternalTasks;

/// <summary>
/// Delivers every custom task to the gRPC workers. Implicitly subscribed to the
/// <c>events.ExternalTask</c> stream, keyed by activity instance id — one activation per task, so
/// the tasks of one instance run in parallel (#815) — it takes the <see cref="ExecuteCustomTaskEvent"/>, assigns it to a connected
/// <see cref="IExternalTaskWorker"/> of its task type, waits for the worker's
/// <see cref="Complete"/> / <see cref="Fail"/> and then completes or fails the activity. After
/// <see cref="Defer"/> it just stops: the activity stays active until a REST report settles it.
///
/// The stream is the task's durable queue: the delivery returns only once the activity has
/// been settled, and every way of not getting an outcome — no worker connected, the worker
/// disconnecting (<see cref="Abandon"/>), <see cref="ExternalTaskOptions.ExecutionTimeout"/>,
/// this grain deactivating — rethrows so the stream provider redelivers the task.
/// </summary>
[ImplicitStreamSubscription(WorkflowEventStreams.ExternalTaskStreamNamespace)]
public sealed partial class ExternalTaskDispatcherGrain : Grain, IExternalTaskDispatcherGrain, IAsyncObserver<ExecuteCustomTaskEvent>
{
    private readonly ILogger<ExternalTaskDispatcherGrain> _logger;
    private readonly ExternalTaskOptions _options;
    private readonly Dictionary<Guid, TaskCompletionSource<Outcome>> _pending = [];

    // Cancel-only — no Timer, no unmanaged handles to dispose.
    private CancellationTokenSource? _lifetime;

    public ExternalTaskDispatcherGrain(ILogger<ExternalTaskDispatcherGrain> logger, IOptions<ExternalTaskOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();

        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventStreams.ExternalTaskStreamNamespace, this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<ExecuteCustomTaskEvent>(streamId);
        foreach (var handle in await stream.GetAllSubscriptionHandles())
            await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        await base.OnActivateAsync(cancellationToken);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Ends the wait in DispatchAsync: the task is redelivered after reactivation.
        _lifetime?.Cancel();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async Task OnNextAsync(ExecuteCustomTaskEvent item, StreamSequenceToken? token = null)
    {
        await HandleAsync(item);

        // Handled — completed, failed or skipped: free the activation now (#922). The grain is keyed
        // by activity instance, so idle collection would keep one activation per task for the whole
        // collection age (15 min). A rethrown failure skips this: the redelivery comes back.
        DeactivateOnIdle();
    }

    public Task<bool> Complete(Guid dispatchId, ExpandoObject result) =>
        Task.FromResult(_pending.TryGetValue(dispatchId, out var pending) && pending.TrySetResult(new Outcome(result, null, null)));

    public Task<bool> Fail(Guid dispatchId, string code, string message) =>
        Task.FromResult(_pending.TryGetValue(dispatchId, out var pending) && pending.TrySetResult(new Outcome(null, code, message)));

    public Task<bool> Defer(Guid dispatchId) =>
        Task.FromResult(_pending.TryGetValue(dispatchId, out var pending) && pending.TrySetResult(Outcome.Deferred));

    public Task Abandon(Guid dispatchId)
    {
        if (_pending.TryGetValue(dispatchId, out var pending))
            pending.TrySetException(new ExternalTaskDeliveryException("The worker disconnected before reporting the task's outcome."));
        return Task.CompletedTask;
    }

    private async Task HandleAsync(ExecuteCustomTaskEvent item)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.ActivityId,
            item.BusinessId);

        LogHandling(item.ActivityId, item.TaskType);

        var workflowInstance = GrainFactory.GetGrain<IWorkflowInstanceCallback>(item.WorkflowInstanceId);

        // Workers of one type can declare different variables during a rolling upgrade: read what
        // covers them all, and give each only its own (DispatchAsync).
        var workers = await GetWorkers(item.TaskType);

        // Pre-dispatch activity check (a read, not a lock): the stream is at-least-once, so a
        // redelivered event can arrive after the activity already finished — skip it. It does not
        // stop a re-run after an interrupted execution; workers still need an idempotency key
        // (activity_instance_id) for external side effects.
        ExpandoObject? variables;
        try
        {
            variables = await workflowInstance.GetVariablesIfActive(
                item.ActivityInstanceId, item.VariablesId, VariableSelection.Union(workers.Select(w => w.Variables)));
        }
        catch (Exception ex) when (!IsTransient(ex))
        {
            // Deterministic (e.g. unknown variable scope): redelivery cannot fix it — fail visibly.
            await FailActivityAsync(workflowInstance, item, ex);
            return;
        }
        catch (Exception ex)
        {
            // No worker has run: let the stream provider redeliver instead of failing the activity.
            LogTransientCallFailed(ex, item.ActivityId, nameof(IWorkflowInstanceCallback.GetVariablesIfActive));
            throw;
        }

        if (variables is null)
        {
            LogSkippedInactiveActivity(item.ActivityId, item.ActivityInstanceId, item.TaskType);
            return;
        }

        var outcome = await DispatchAsync(item, workers, variables);

        if (ReferenceEquals(outcome, Outcome.Deferred))
        {
            LogDeferred(item.ActivityId, item.TaskType);
            return;
        }

        if (outcome.Result is null)
        {
            await FailActivityAsync(workflowInstance, item,
                new CustomTaskFailedActivityException(outcome.ErrorCode!, outcome.ErrorMessage!));
            return;
        }

        try
        {
            await workflowInstance.CompleteActivity(item.ActivityId, item.ActivityInstanceId, outcome.Result);
        }
        catch (Exception ex) when (!IsTransient(ex))
        {
            // Deterministic (e.g. outputs that cannot be serialized): fail visibly.
            await FailActivityAsync(workflowInstance, item, ex);
        }
        catch (Exception ex)
        {
            // Redelivery runs the task again (the activity is still active) — the documented
            // reason workers need an idempotency key.
            LogTransientCallFailed(ex, item.ActivityId, nameof(IWorkflowInstanceCallback.CompleteActivity));
            throw;
        }
    }

    /// <summary>
    /// Assigns the task to a connected worker of its type (tried in random order until one
    /// accepts) and waits for its outcome. Throws when there is none to wait for, so the stream
    /// redelivers the task.
    /// </summary>
    private async Task<Outcome> DispatchAsync(ExecuteCustomTaskEvent item, List<ExternalTaskPoolWorker> workers, ExpandoObject variables)
    {
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(workers));

        var dispatchId = Guid.NewGuid();
        var taskToken = ExternalTaskToken.Format(item.ActivityInstanceId, dispatchId);
        var assignment = new ExternalTaskAssignment(
            taskToken, item.TaskType, item.WorkflowInstanceId, item.WorkflowId, item.ProcessDefinitionId,
            item.ActivityInstanceId, item.ActivityId, item.BusinessId,
            ExternalTaskJson.Serialize(item.Inputs), VariablesJson: string.Empty);
        var pending = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[dispatchId] = pending;
        try
        {
            IExternalTaskWorker? assignedTo = null;
            foreach (var (worker, selection) in workers)
            {
                var variablesJson = ExternalTaskJson.Serialize(selection.Apply(variables));
                try
                {
                    if (await worker.Assign(assignment with { VariablesJson = variablesJson }))
                    {
                        assignedTo = worker;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogAssignFailed(ex, item.ActivityId, item.TaskType);
                }
            }

            if (assignedTo is null)
            {
                LogNoWorker(item.ActivityId, item.TaskType, workers.Count);
                throw new ExternalTaskDeliveryException($"No connected worker accepted external-task type '{item.TaskType}'.");
            }

            LogAssigned(item.ActivityId, item.TaskType, dispatchId);
            try
            {
                return await pending.Task.WaitAsync(_options.ExecutionTimeout, _lifetime!.Token);
            }
            catch (TimeoutException)
            {
                LogTimedOut(item.ActivityId, item.TaskType, _options.ExecutionTimeout);
                await CancelAsync(assignedTo, taskToken);
                throw;
            }
            catch (OperationCanceledException) when (_lifetime!.IsCancellationRequested)
            {
                LogCancelledOnDeactivation(item.ActivityId, item.TaskType);
                await CancelAsync(assignedTo, taskToken);
                throw;
            }
            catch (ExternalTaskDeliveryException)
            {
                LogWorkerLost(item.ActivityId, item.TaskType);
                throw;
            }
        }
        finally
        {
            _pending.Remove(dispatchId);
        }
    }

    /// <summary>The workers claiming exactly this type, else those on its <c>scheme:*</c> wildcard.</summary>
    private async Task<List<ExternalTaskPoolWorker>> GetWorkers(string taskType)
    {
        var workers = await GrainFactory
            .GetGrain<IExternalTaskWorkerPoolGrain>(ExternalTaskWorkerPoolGrain.KeyFor(taskType))
            .GetWorkers();
        return workers.Count == 0 && ExternalTaskWorkerPoolGrain.WildcardKeyFor(taskType) is { } wildcard
            ? await GrainFactory.GetGrain<IExternalTaskWorkerPoolGrain>(wildcard).GetWorkers()
            : workers;
    }

    private async Task CancelAsync(IExternalTaskWorker worker, string taskToken)
    {
        try
        {
            await worker.Cancel(taskToken);
        }
        catch (Exception ex)
        {
            LogCancelFailed(ex);
        }
    }

    /// <summary>
    /// Infrastructure failures of a grain call (timeout, silo unavailable, message rejected) that
    /// stream redelivery can recover from. Exceptions thrown by the grain method itself arrive
    /// with their own type and are treated as deterministic.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex is TimeoutException or OrleansException;

    private async Task FailActivityAsync(
        IWorkflowInstanceCallback workflowInstance, ExecuteCustomTaskEvent item, Exception ex)
    {
        LogExecuteFailed(ex, item.ActivityId, item.TaskType);
        try
        {
            await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
        }
        catch (Exception failEx)
        {
            LogFailActivityFailed(failEx, item.ActivityId);
            FleansDiagnostics.OnWorkflowStalled("custom_task");
            // Redelivery duplicates are absorbed by the HasActiveEntry guards in
            // WorkflowInstance.CompleteActivity / FailActivity, backed by the domain-level
            // entry.IsCompleted early-return in WorkflowExecution.
            // See docs/conventions/invariants.md "Stream-redelivery idempotency".
            throw;
        }
    }

    public Task OnCompletedAsync()
    {
        LogStreamCompleted();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    /// <summary>A worker's report: <see cref="Result"/> on completion, the error otherwise.</summary>
    private sealed record Outcome(ExpandoObject? Result, string? ErrorCode, string? ErrorMessage)
    {
        /// <summary>The worker took the task; its outcome arrives out of band.</summary>
        public static readonly Outcome Deferred = new(null, null, null);
    }

    [LoggerMessage(EventId = 14500, Level = LogLevel.Information,
        Message = "Dispatching external task for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogHandling(string activityId, string taskType);

    [LoggerMessage(EventId = 14501, Level = LogLevel.Debug,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) assigned as dispatch {DispatchId}")]
    private partial void LogAssigned(string activityId, string taskType, Guid dispatchId);

    [LoggerMessage(EventId = 14502, Level = LogLevel.Warning,
        Message = "No connected worker accepted the external task for activity {ActivityId} (taskType={TaskType}, {WorkerCount} workers in its pool) — rethrowing for stream redelivery")]
    private partial void LogNoWorker(string activityId, string taskType, int workerCount);

    [LoggerMessage(EventId = 14503, Level = LogLevel.Warning,
        Message = "Assigning the external task for activity {ActivityId} (taskType={TaskType}) to a worker failed; trying the next one")]
    private partial void LogAssignFailed(Exception ex, string activityId, string taskType);

    [LoggerMessage(EventId = 14504, Level = LogLevel.Warning,
        Message = "The worker running the external task for activity {ActivityId} (taskType={TaskType}) disconnected — rethrowing for stream redelivery")]
    private partial void LogWorkerLost(string activityId, string taskType);

    [LoggerMessage(EventId = 14505, Level = LogLevel.Warning,
        Message = "No outcome for the external task for activity {ActivityId} (taskType={TaskType}) within {Timeout} — cancelled on the worker, rethrowing for stream redelivery")]
    private partial void LogTimedOut(string activityId, string taskType, TimeSpan timeout);

    [LoggerMessage(EventId = 14506, Level = LogLevel.Information,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) cancelled on grain deactivation — the stream provider will redeliver it after reactivation")]
    private partial void LogCancelledOnDeactivation(string activityId, string taskType);

    [LoggerMessage(EventId = 14507, Level = LogLevel.Warning,
        Message = "Telling a worker to cancel an external task failed")]
    private partial void LogCancelFailed(Exception ex);

    [LoggerMessage(EventId = 14508, Level = LogLevel.Information,
        Message = "External-task event for activity {ActivityId} (instance {ActivityInstanceId}, taskType={TaskType}) skipped — activity is no longer active (redelivered or duplicate event)")]
    private partial void LogSkippedInactiveActivity(string activityId, Guid activityInstanceId, string taskType);

    [LoggerMessage(EventId = 14509, Level = LogLevel.Warning,
        Message = "External-task call {Call} for activity {ActivityId} failed transiently — rethrowing for stream redelivery")]
    private partial void LogTransientCallFailed(Exception ex, string activityId, string call);

    [LoggerMessage(EventId = 14510, Level = LogLevel.Error,
        Message = "External task failed for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogExecuteFailed(Exception ex, string activityId, string taskType);

    [LoggerMessage(EventId = 14511, Level = LogLevel.Critical,
        Message = "FailActivity call itself failed for activity {ActivityId} — workflow may be stalled")]
    private partial void LogFailActivityFailed(Exception ex, string activityId);

    [LoggerMessage(EventId = 14512, Level = LogLevel.Information,
        Message = "External-task event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 14513, Level = LogLevel.Error,
        Message = "External-task event stream error")]
    private partial void LogStreamError(Exception ex);

    [LoggerMessage(EventId = 14514, Level = LogLevel.Information,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) deferred by its worker — the activity stays active until reported through the REST API")]
    private partial void LogDeferred(string activityId, string taskType);
}

/// <summary>
/// An external task got no outcome from a worker (none connected, or it disconnected); the stream
/// redelivers the task.
/// </summary>
public sealed class ExternalTaskDeliveryException(string message) : Exception(message);
