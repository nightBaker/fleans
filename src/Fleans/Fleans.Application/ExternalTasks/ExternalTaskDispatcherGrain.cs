using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.CustomTasks;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Application.Observability;
using Fleans.Domain;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
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
///
/// Before a worker gets the task, the delivery claims it on the workflow instance (#818), so two
/// activations of this grain during a membership change cannot both run it. The claim is a lease
/// of <see cref="ExternalTaskOptions.WorkerTimeout"/>, renewed every
/// <see cref="ExternalTaskOptions.HeartbeatInterval"/> while the worker runs it: a delivery whose
/// silo died stops holding the task after one lease. A delivery that rethrows releases its claim,
/// so the redelivery can claim it at once; a deferred task's claim holds until it is settled.
/// </summary>
[ImplicitStreamSubscription(WorkflowEventStreams.ExternalTaskStreamNamespace)]
public sealed partial class ExternalTaskDispatcherGrain : Grain, IExternalTaskDispatcherGrain, IAsyncObserver<ExecuteCustomTaskEvent>
{
    /// <summary>The error a task fails with when its inputs don't fit the connected plugin version (#1051).</summary>
    public const string PluginInputsInvalidCode = "PLUGIN_INPUTS_INVALID";

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

        var workflowInstance = GrainFactory.GetGrain<IWorkflowInstanceGrain>(item.WorkflowInstanceId);

        // Workers of one version can declare different variables during a rolling upgrade: read what
        // covers them all, and give each only its own (DispatchAsync).
        var (workers, hasContract) = await GetWorkers(item.TaskType, item.TaskVersion);

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

        var inputsJson = ExternalTaskJson.Serialize(item.Inputs);
        if (hasContract && FindInputViolations(workers, inputsJson) is { Count: > 0 } violations)
        {
            // Deterministic for the connected version, so no redelivery and no worker is called. A
            // process on latest can reach this after a plugin update or while only an older version
            // is connected.
            await FailActivityAsync(workflowInstance, item, new CustomTaskFailedActivityException(
                PluginInputsInvalidCode,
                $"Custom task '{item.TaskType}' ({workers[0].Version ?? "without a version"}): {string.Join("; ", violations)}."));
            return;
        }

        if (workers.Count == 0)
        {
            // Checked before the claim: a task waiting for its first worker retries often, and
            // each retry would journal a claim and a release.
            LogNoWorker(item.ActivityId, item.TaskType, 0);
            throw new ExternalTaskDeliveryException($"No connected worker accepted external-task type '{item.TaskType}'.");
        }

        if (await TryClaimAsync(workflowInstance, item) is not { } dispatchId)
            return;

        Outcome outcome;
        try
        {
            outcome = await DispatchAsync(item, workflowInstance, dispatchId, workers, variables, inputsJson);
        }
        catch
        {
            // Rethrown for redelivery: let it claim the task now instead of after the lease.
            await ReleaseClaimAsync(workflowInstance, item, dispatchId);
            throw;
        }

        if (ReferenceEquals(outcome, Outcome.Deferred))
        {
            LogDeferred(item.ActivityId, item.TaskType);
            // The task waits for a REST report now, not for a worker: hold it until then.
            try
            {
                await workflowInstance.ClaimCustomTask(item.ActivityInstanceId, dispatchId, lease: null);
            }
            catch (Exception ex)
            {
                LogClaimCallFailed(ex, item.ActivityId);
            }
            return;
        }

        // Settled or deferred meanwhile: nothing left to run.
        if (ReferenceEquals(outcome, Outcome.ClaimLost))
            return;

        // Another dispatch took the task after this one's lease ran out. Redeliver rather than
        // trust it to finish: if it dies, the redelivery is what runs the task again.
        if (ReferenceEquals(outcome, Outcome.ClaimHeld))
            throw new ExternalTaskDeliveryException(
                $"Another delivery of the custom task for activity '{item.ActivityId}' took it over.");

        if (outcome.Result is null)
        {
            await FailActivityAsync(workflowInstance, item,
                new CustomTaskFailedActivityException(outcome.ErrorCode!, outcome.ErrorMessage!), dispatchId);
            return;
        }

        try
        {
            await workflowInstance.CompleteActivity(item.ActivityId, item.ActivityInstanceId, outcome.Result);
        }
        catch (Exception ex) when (!IsTransient(ex))
        {
            // Deterministic (e.g. outputs that cannot be serialized): fail visibly.
            await FailActivityAsync(workflowInstance, item, ex, dispatchId);
        }
        catch (Exception ex)
        {
            // Redelivery runs the task again (the activity is still active) — the documented
            // reason workers need an idempotency key.
            LogTransientCallFailed(ex, item.ActivityId, nameof(IWorkflowInstanceCallback.CompleteActivity));
            await ReleaseClaimAsync(workflowInstance, item, dispatchId);
            throw;
        }
    }

    /// <summary>
    /// Claims the task for this dispatch (#818) and returns the dispatch id that holds it: the
    /// reservation the instance published the task with on its first delivery, a new id otherwise.
    /// <c>null</c> when there is nothing to run: the activity is settled, or deferred and waiting
    /// for its REST report. Throws for redelivery while another dispatch holds it, so the task is
    /// run again if that dispatch dies.
    /// </summary>
    private async Task<Guid?> TryClaimAsync(IWorkflowInstanceGrain workflowInstance, ExecuteCustomTaskEvent item)
    {
        CustomTaskClaimResult claim;
        Guid dispatchId;
        try
        {
            (claim, dispatchId) = await workflowInstance.TakeCustomTask(item.ActivityInstanceId, item.DispatchId);
        }
        catch (Exception ex) when (!IsTransient(ex))
        {
            // Deterministic: redelivery cannot fix it — fail visibly, as the activity check does.
            await FailActivityAsync(workflowInstance, item, ex);
            return null;
        }
        catch (Exception ex)
        {
            // No worker has run: the redelivery claims again. A claim taken before the call failed
            // (a late answer) is not released — this delivery cannot tell it from another
            // delivery's — so the redelivery waits for its lease.
            LogTransientCallFailed(ex, item.ActivityId, nameof(IWorkflowInstanceGrain.TakeCustomTask));
            throw;
        }

        switch (claim)
        {
            case CustomTaskClaimResult.Granted:
                return dispatchId;
            case CustomTaskClaimResult.Finished:
                LogSkippedInactiveActivity(item.ActivityId, item.ActivityInstanceId, item.TaskType);
                return null;
            case CustomTaskClaimResult.Deferred:
                LogSkippedDeferred(item.ActivityId, item.TaskType);
                return null;
            default:
                LogClaimHeld(item.ActivityId, item.TaskType);
                throw new ExternalTaskDeliveryException(
                    $"Another delivery of the custom task for activity '{item.ActivityId}' is running it.");
        }
    }

    /// <summary>Best effort: a claim not released runs out after its lease.</summary>
    private async Task ReleaseClaimAsync(IWorkflowInstanceGrain workflowInstance, ExecuteCustomTaskEvent item, Guid dispatchId)
    {
        try
        {
            await workflowInstance.ReleaseCustomTaskClaim(item.ActivityInstanceId, dispatchId);
        }
        catch (Exception ex)
        {
            LogClaimCallFailed(ex, item.ActivityId);
        }
    }

    /// <summary>
    /// Assigns the task to a connected worker of its type (tried in random order until one
    /// accepts) and waits for its outcome. Throws when there is none to wait for, so the stream
    /// redelivers the task.
    /// </summary>
    private async Task<Outcome> DispatchAsync(
        ExecuteCustomTaskEvent item, IWorkflowInstanceGrain workflowInstance, Guid dispatchId,
        List<ExternalTaskPoolWorker> workers, ExpandoObject variables, string inputsJson)
    {
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(workers));

        var taskToken = ExternalTaskToken.Format(item.ActivityInstanceId, dispatchId);
        var assignment = new ExternalTaskAssignment(
            taskToken, item.TaskType, item.WorkflowInstanceId, item.WorkflowId, item.ProcessDefinitionId,
            item.ActivityInstanceId, item.ActivityId, item.BusinessId,
            inputsJson, VariablesJson: string.Empty);
        var pending = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[dispatchId] = pending;
        try
        {
            IExternalTaskWorker? assignedTo = null;
            foreach (var (worker, selection, _, _) in workers)
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
                var outcome = await WaitForOutcomeAsync(pending.Task, item, workflowInstance, dispatchId);
                if (ReferenceEquals(outcome, Outcome.ClaimLost) || ReferenceEquals(outcome, Outcome.ClaimHeld))
                    await CancelAsync(assignedTo, taskToken);
                return outcome;
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

    /// <summary>
    /// Waits up to <see cref="ExternalTaskOptions.ExecutionTimeout"/> for the worker's outcome,
    /// renewing the claim every <see cref="ExternalTaskOptions.HeartbeatInterval"/>. A renewal call
    /// is bounded by that interval and a failed one is tried again at once, so two tries fit in one
    /// <see cref="ExternalTaskOptions.WorkerTimeout"/> lease. When a renewal finds the claim gone,
    /// an outcome the worker has already reported still wins (completing is idempotent); otherwise
    /// <see cref="Outcome.ClaimLost"/> (settled or deferred) or <see cref="Outcome.ClaimHeld"/>
    /// (another dispatch holds it).
    /// </summary>
    private static readonly TimeSpan RenewalRetryDelay = TimeSpan.FromSeconds(1);

    private async Task<Outcome> WaitForOutcomeAsync(
        Task<Outcome> outcome, ExecuteCustomTaskEvent item, IWorkflowInstanceGrain workflowInstance, Guid dispatchId)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var renewedAt = started;
        while (true)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var left = _options.ExecutionTimeout - elapsed;
            var untilRenewal = _options.HeartbeatInterval - System.Diagnostics.Stopwatch.GetElapsedTime(renewedAt);
            if (left <= untilRenewal)
                return await outcome.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, _lifetime!.Token);

            if (untilRenewal > TimeSpan.Zero)
            {
                try
                {
                    return await outcome.WaitAsync(untilRenewal, _lifetime!.Token);
                }
                catch (TimeoutException)
                {
                    // Not done yet: renew below.
                }
            }

            var attemptAt = System.Diagnostics.Stopwatch.GetTimestamp();
            CustomTaskClaimResult renewed;
            try
            {
                renewed = await workflowInstance
                    .ClaimCustomTask(item.ActivityInstanceId, dispatchId, _options.WorkerTimeout)
                    .WaitAsync(_options.HeartbeatInterval, _lifetime!.Token);
            }
            catch (OperationCanceledException) when (_lifetime!.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Try again shortly, while the lease still holds (a second before, so a call that
                // fails fast does not spin).
                LogClaimCallFailed(ex, item.ActivityId);
                try
                {
                    return await outcome.WaitAsync(RenewalRetryDelay, _lifetime!.Token);
                }
                catch (TimeoutException)
                {
                    continue;
                }
            }

            if (renewed == CustomTaskClaimResult.Granted)
            {
                renewedAt = attemptAt;
                continue;
            }

            if (outcome.IsCompletedSuccessfully)
                return outcome.Result;
            LogClaimLost(item.ActivityId, item.TaskType, renewed);
            return renewed == CustomTaskClaimResult.Held ? Outcome.ClaimHeld : Outcome.ClaimLost;
        }
    }

    /// <summary>
    /// The workers of the version the task selects among those claiming exactly this type. When none
    /// claims it, those on its <c>scheme:*</c> wildcard, which has no versions and no contract.
    /// </summary>
    private async Task<(List<ExternalTaskPoolWorker> Workers, bool HasContract)> GetWorkers(string taskType, string? version)
    {
        var workers = await GrainFactory
            .GetGrain<IExternalTaskWorkerPoolGrain>(ExternalTaskWorkerPoolGrain.KeyFor(taskType))
            .GetWorkers();
        if (workers.Count == 0 && ExternalTaskWorkerPoolGrain.WildcardKeyFor(taskType) is { } wildcard)
            return (await GrainFactory.GetGrain<IExternalTaskWorkerPoolGrain>(wildcard).GetWorkers(), false);
        return (CustomTaskVersions.SelectGroup(workers, w => w.Version, version), true);
    }

    /// <summary>
    /// What's wrong with the inputs for this group of one version; <c>null</c> when it has no contract:
    /// no schema, or workers without a version that disagree.
    /// </summary>
    private static List<string>? FindInputViolations(List<ExternalTaskPoolWorker> workers, string inputsJson)
    {
        if (workers.Count == 0 || workers[0].ParameterSchema is not { } schema
            || workers.Any(w => !CustomTaskParameterSchema.HaveSameContract(w.ParameterSchema, schema)))
        {
            return null;
        }
        return CustomTaskInputRules.CheckValues(schema, JObject.Parse(inputsJson));
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

    /// <summary>
    /// Fails the activity. When that call itself fails, rethrows for redelivery, first giving up
    /// <paramref name="dispatchId"/>'s claim (if any) so the redelivery is not held by it.
    /// </summary>
    private async Task FailActivityAsync(
        IWorkflowInstanceGrain workflowInstance, ExecuteCustomTaskEvent item, Exception ex, Guid? dispatchId = null)
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
            if (dispatchId is { } claimed)
                await ReleaseClaimAsync(workflowInstance, item, claimed);
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

        /// <summary>The claim is gone because the task was settled or deferred meanwhile (#818).</summary>
        public static readonly Outcome ClaimLost = new(null, null, null);

        /// <summary>The claim ran out and another dispatch holds the task now (#818).</summary>
        public static readonly Outcome ClaimHeld = new(null, null, null);
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

    [LoggerMessage(EventId = 14515, Level = LogLevel.Information,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) is held by another delivery — rethrowing; the redelivery claims it once that one ends (#818)")]
    private partial void LogClaimHeld(string activityId, string taskType);

    [LoggerMessage(EventId = 14516, Level = LogLevel.Information,
        Message = "External-task event for activity {ActivityId} (taskType={TaskType}) skipped — its worker deferred the task, which waits for a REST report")]
    private partial void LogSkippedDeferred(string activityId, string taskType);

    [LoggerMessage(EventId = 14517, Level = LogLevel.Warning,
        Message = "External task for activity {ActivityId} (taskType={TaskType}) lost its claim ({Claim}) while its worker ran it — cancelled on the worker; redelivered when another delivery holds it")]
    private partial void LogClaimLost(string activityId, string taskType, CustomTaskClaimResult claim);

    [LoggerMessage(EventId = 14518, Level = LogLevel.Warning,
        Message = "Renewing, releasing or holding the claim on the external task for activity {ActivityId} failed; the lease runs out on its own")]
    private partial void LogClaimCallFailed(Exception ex, string activityId);
}

/// <summary>
/// An external task got no outcome from a worker (none connected, or it disconnected); the stream
/// redelivers the task.
/// </summary>
public sealed class ExternalTaskDeliveryException(string message) : Exception(message);
