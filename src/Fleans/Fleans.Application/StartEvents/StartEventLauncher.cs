using System.Dynamic;
using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.Application.StartEvents;

/// <summary>
/// Starts the instances a message, signal or conditional start event creates (#858). The
/// subscribed processes come from this host's <see cref="StartEventSubscriptionCache"/>; each start
/// is one <see cref="IProcessDefinitionGrain.GetStartableDefinition"/> call — the authority on
/// enabled and latest — and one call to the new <c>WorkflowInstance</c>. No grain per event name is
/// on the way, so starts of one name run in parallel on every host.
/// </summary>
public sealed partial class StartEventLauncher
{
    private const int BatchSize = 20;
    private const int HighProcessCountThreshold = 50;

    private readonly IGrainFactory _grainFactory;
    private readonly StartEventSubscriptionCache _subscriptions;
    private readonly WorkerEvaluatorOptions _evaluatorOptions;
    private readonly ILogger<StartEventLauncher> _logger;

    public StartEventLauncher(
        IGrainFactory grainFactory,
        StartEventSubscriptionCache subscriptions,
        IOptions<WorkerEvaluatorOptions> evaluatorOptions,
        ILogger<StartEventLauncher> logger)
    {
        _grainFactory = grainFactory;
        _subscriptions = subscriptions;
        _evaluatorOptions = evaluatorOptions.Value;
        _logger = logger;
    }

    /// <summary>Starts one instance of every enabled process with a message start event for <paramref name="messageName"/>.</summary>
    public async Task<List<Guid>> StartByMessage(string messageName, ExpandoObject variables, string? businessId = null)
    {
        // Reject an invalid id up front: inside the per-process catch below it would surface as
        // "not delivered" instead of a validation error.
        BusinessIdRules.Validate(businessId);

        var plan = await PlanMessageStarts(messageName);
        return await StartEach(StartEventKind.Message, messageName, plan, variables, businessId);
    }

    /// <summary>
    /// Picks the instance id each process subscribed to <paramref name="messageName"/> will get,
    /// without starting anything — so a caller can persist the ids first and start them with
    /// <see cref="StartPlanned"/>, repeatedly if it must (#743).
    /// </summary>
    public async Task<List<PlannedStart>> PlanMessageStarts(string messageName)
        => Plan(StartEventKind.Message, messageName, await _subscriptions.GetMessageStartProcesses(messageName));

    /// <summary>
    /// Starts the instances of a <see cref="PlanMessageStarts"/> plan. Starting a planned instance
    /// that already started is a no-op (<c>StartWorkflow</c> is idempotent), so a plan can be re-run.
    /// </summary>
    public Task<List<Guid>> StartPlanned(
        string messageName, IReadOnlyList<PlannedStart> plan, ExpandoObject variables, string? businessId)
    {
        BusinessIdRules.Validate(businessId);
        return StartEach(StartEventKind.Message, messageName, plan, variables, businessId);
    }

    /// <summary>Starts one instance of every enabled process with a signal start event for <paramref name="signalName"/>.</summary>
    public async Task<List<Guid>> StartBySignal(string signalName)
    {
        var plan = Plan(StartEventKind.Signal, signalName, await _subscriptions.GetSignalStartProcesses(signalName));
        return await StartEach(StartEventKind.Signal, signalName, plan, variables: null, businessId: null);
    }

    /// <summary>
    /// Evaluates every conditional start event — or only those of <paramref name="workflowId"/> —
    /// against <paramref name="variables"/> and starts an instance for each that holds.
    /// </summary>
    public async Task<EvaluateConditionsResult> StartByConditions(string? workflowId, ExpandoObject variables)
    {
        IEnumerable<ConditionalStartEntry> entries = await _subscriptions.GetConditionalStarts();
        if (!string.IsNullOrWhiteSpace(workflowId))
            entries = entries.Where(e => e.ProcessDefinitionKey == workflowId);

        var startedIds = new List<Guid>();
        var errors = new List<string>();

        // One definition read per process, not per conditional start: concurrently in bounded
        // batches, like message and signal starts (#159).
        foreach (var batch in entries.GroupBy(e => e.ProcessDefinitionKey).Chunk(BatchSize))
        {
            var results = await Task.WhenAll(batch.Select(process => StartProcessIfConditionsHold(process, variables)));
            foreach (var result in results.SelectMany(r => r))
            {
                if (result.InstanceId is { } instanceId)
                    startedIds.Add(instanceId);
                if (result.Error is not null)
                    errors.Add(result.Error);
            }
        }

        return new EvaluateConditionsResult(startedIds, errors.Count > 0 ? errors : null);
    }

    private async Task<(Guid? InstanceId, string? Error)[]> StartProcessIfConditionsHold(
        IGrouping<string, ConditionalStartEntry> process, ExpandoObject variables)
    {
        IWorkflowDefinition? definition;
        try
        {
            definition = await _grainFactory.GetGrain<IProcessDefinitionGrain>(process.Key).GetStartableDefinition();
        }
        catch (Exception ex)
        {
            return process.Select(entry => Failed(entry, ex)).ToArray();
        }

        return await Task.WhenAll(process.Select(async entry =>
        {
            try
            {
                return (InstanceId: await StartIfConditionHolds(entry, definition, variables), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return Failed(entry, ex);
            }
        }));
    }

    private (Guid? InstanceId, string? Error) Failed(ConditionalStartEntry entry, Exception ex)
    {
        LogConditionalStartFailed(entry.ProcessDefinitionKey, entry.ActivityId, ex);
        return (null, $"{entry.ProcessDefinitionKey}:{entry.ActivityId}: {ex.Message}");
    }

    private List<PlannedStart> Plan(StartEventKind kind, string eventName, IReadOnlyList<string> processKeys)
    {
        if (processKeys.Count == 0)
            LogNoSubscribedProcesses(kind, eventName);
        else if (processKeys.Count > HighProcessCountThreshold)
            LogHighProcessCount(kind, eventName, processKeys.Count, HighProcessCountThreshold);

        // A random (v4) id on purpose: it is the journal's shard key (docs/conventions/invariants.md).
        return processKeys.Select(key => new PlannedStart(key, Guid.NewGuid())).ToList();
    }

    private async Task<List<Guid>> StartEach(
        StartEventKind kind, string eventName, IReadOnlyList<PlannedStart> plan,
        ExpandoObject? variables, string? businessId)
    {
        var created = new List<Guid>();
        foreach (var batch in plan.Chunk(BatchSize))
        {
            var results = await Task.WhenAll(batch.Select(start => StartOne(kind, eventName, start, variables, businessId)));
            created.AddRange(results.Where(id => id.HasValue).Select(id => id!.Value));
        }

        return created;
    }

    private async Task<Guid?> StartOne(
        StartEventKind kind, string eventName, PlannedStart start,
        ExpandoObject? variables, string? businessId)
    {
        var (processDefinitionKey, instanceId) = start;
        try
        {
            var definition = await _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefinitionKey)
                .GetStartableDefinition();
            if (definition is null)
            {
                LogProcessDisabledSkipped(kind, eventName, processDefinitionKey);
                return null;
            }

            var startActivityId = kind == StartEventKind.Message
                ? FindMessageStartActivityId(definition, eventName)
                : FindSignalStartActivityId(definition, eventName);
            if (startActivityId is null)
            {
                // A cached subscription the latest version no longer has: refreshed on the next reload.
                LogStartEventNotInLatestVersion(kind, eventName, processDefinitionKey);
                return null;
            }

            await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId)
                .StartWorkflow(definition, startActivityId, businessId, variables);

            LogStartEventFired(kind, eventName, processDefinitionKey, instanceId);
            return instanceId;
        }
        catch (Exception ex)
        {
            LogStartEventFailed(kind, eventName, processDefinitionKey, ex);
            return null;
        }
    }

    private async Task<Guid?> StartIfConditionHolds(
        ConditionalStartEntry entry, IWorkflowDefinition? definition, ExpandoObject variables)
    {
        if (definition is null)
        {
            LogConditionalProcessDisabledSkipped(entry.ProcessDefinitionKey, entry.ActivityId);
            return null;
        }

        // The condition comes from the latest definition, not the cache, so a cached entry can
        // never evaluate an expression a redeploy replaced.
        if (definition.FindActivity(entry.ActivityId) is not ConditionalStartEvent startEvent)
        {
            LogConditionalStartNotInLatestVersion(entry.ProcessDefinitionKey, entry.ActivityId);
            return null;
        }

        var evaluator = _grainFactory.GetGrain<IConditionExpressionEvaluatorGrain>(_evaluatorOptions.NextKey());
        if (!await evaluator.Evaluate(startEvent.ConditionExpression, variables))
        {
            LogConditionFalse(entry.ProcessDefinitionKey, entry.ActivityId);
            return null;
        }

        var instanceId = Guid.NewGuid();
        await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId)
            .StartWorkflow(definition, entry.ActivityId, variables: variables);

        LogConditionalStartFired(entry.ProcessDefinitionKey, entry.ActivityId, instanceId);
        return instanceId;
    }

    internal static string? FindMessageStartActivityId(IWorkflowDefinition definition, string messageName)
    {
        foreach (var activity in definition.Activities.OfType<MessageStartEvent>())
        {
            if (definition.FindMessageDefinition(activity.MessageDefinitionId)?.Name == messageName)
                return activity.ActivityId;
        }

        // A Multiple Start Event subscribes its message definitions too
        // (IWorkflowDefinition.GetMessageStartEventNames), so it must be resolvable here.
        foreach (var multiStart in definition.Activities.OfType<MultipleStartEvent>())
        {
            foreach (var msgEventDef in multiStart.Definitions.OfType<MessageEventDef>())
            {
                if (definition.FindMessageDefinition(msgEventDef.MessageDefinitionId)?.Name == messageName)
                    return multiStart.ActivityId;
            }
        }
        return null;
    }

    internal static string? FindSignalStartActivityId(IWorkflowDefinition definition, string signalName)
    {
        foreach (var activity in definition.Activities.OfType<SignalStartEvent>())
        {
            if (definition.FindSignalDefinition(activity.SignalDefinitionId)?.Name == signalName)
                return activity.ActivityId;
        }

        // A Multiple Start Event subscribes its signal definitions too
        // (IWorkflowDefinition.GetSignalStartEventNames), so it must be resolvable here.
        foreach (var multiStart in definition.Activities.OfType<MultipleStartEvent>())
        {
            foreach (var sigEventDef in multiStart.Definitions.OfType<SignalEventDef>())
            {
                if (definition.FindSignalDefinition(sigEventDef.SignalDefinitionId)?.Name == signalName)
                    return multiStart.ActivityId;
            }
        }
        return null;
    }

    private enum StartEventKind
    {
        Message,
        Signal
    }

    [LoggerMessage(EventId = 9370, Level = LogLevel.Information,
        Message = "{Kind} start event '{EventName}' started process {ProcessDefinitionKey} as instance {InstanceId}")]
    private partial void LogStartEventFired(StartEventKind kind, string eventName, string processDefinitionKey, Guid instanceId);

    [LoggerMessage(EventId = 9371, Level = LogLevel.Warning,
        Message = "No process is subscribed to {Kind} start event '{EventName}'")]
    private partial void LogNoSubscribedProcesses(StartEventKind kind, string eventName);

    [LoggerMessage(EventId = 9372, Level = LogLevel.Error,
        Message = "{Kind} start event '{EventName}' failed to start process {ProcessDefinitionKey}")]
    private partial void LogStartEventFailed(StartEventKind kind, string eventName, string processDefinitionKey, Exception ex);

    [LoggerMessage(EventId = 9373, Level = LogLevel.Warning,
        Message = "Skipping disabled process {ProcessDefinitionKey} for {Kind} start event '{EventName}'")]
    private partial void LogProcessDisabledSkipped(StartEventKind kind, string eventName, string processDefinitionKey);

    [LoggerMessage(EventId = 9374, Level = LogLevel.Warning,
        Message = "{Kind} start event '{EventName}' has {ProcessCount} subscribed processes (threshold: {Threshold}) — starting in batches")]
    private partial void LogHighProcessCount(StartEventKind kind, string eventName, int processCount, int threshold);

    [LoggerMessage(EventId = 9375, Level = LogLevel.Debug,
        Message = "Skipping process {ProcessDefinitionKey}: its latest version has no {Kind} start event '{EventName}'")]
    private partial void LogStartEventNotInLatestVersion(StartEventKind kind, string eventName, string processDefinitionKey);

    [LoggerMessage(EventId = 9376, Level = LogLevel.Information,
        Message = "Conditional start event fired for process {ProcessDefinitionKey}, activity {ActivityId}, created instance {InstanceId}")]
    private partial void LogConditionalStartFired(string processDefinitionKey, string activityId, Guid instanceId);

    [LoggerMessage(EventId = 9377, Level = LogLevel.Debug,
        Message = "Condition evaluated false for process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogConditionFalse(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9378, Level = LogLevel.Warning,
        Message = "Skipping disabled process {ProcessDefinitionKey} for conditional start event activity {ActivityId}")]
    private partial void LogConditionalProcessDisabledSkipped(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9379, Level = LogLevel.Debug,
        Message = "Skipping process {ProcessDefinitionKey}: its latest version has no conditional start event {ActivityId}")]
    private partial void LogConditionalStartNotInLatestVersion(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9380, Level = LogLevel.Error,
        Message = "Conditional evaluation failed for process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogConditionalStartFailed(string processDefinitionKey, string activityId, Exception ex);
}
