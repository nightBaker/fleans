using Fleans.Application.Deployments;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Application.QueryModels;
using Fleans.Application.StartEvents;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[Reentrant]
[CorePlacement]
public partial class ProcessDefinitionGrain : Grain, IProcessDefinitionGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ProcessDefinitionGrain> _logger;
    private readonly IProcessDefinitionRepository _repository;
    private readonly WorkflowExpressionValidator _expressionValidator;

    private readonly Dictionary<string, ProcessDefinition> _byId = new(StringComparer.Ordinal);
    private readonly List<ProcessDefinition> _versions = [];

    public ProcessDefinitionGrain(
        IGrainFactory grainFactory,
        ILogger<ProcessDefinitionGrain> logger,
        IProcessDefinitionRepository repository,
        WorkflowExpressionValidator expressionValidator)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _repository = repository;
        _expressionValidator = expressionValidator;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        var key = this.GetPrimaryKeyString();
        var definitions = await _repository.GetByKeyAsync(key);
        foreach (var def in definitions.OrderBy(d => d.Version))
        {
            _byId[def.ProcessDefinitionId] = def;
            _versions.Add(def);
        }

        LogActivated(key, _versions.Count);
    }

    public async Task<ProcessDefinitionSummary> DeployVersion(WorkflowDefinition workflow, string bpmnXml)
    {
        if (workflow == null)
            throw new ArgumentNullException(nameof(workflow));

        if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
            throw new ArgumentException("WorkflowId cannot be null or empty.", nameof(workflow));

        // Every deploy path ends here, so no version is stored with an expression that does not compile.
        _expressionValidator.Validate(workflow);

        var processDefinitionKey = this.GetPrimaryKeyString();
        var deployedAt = DateTimeOffset.UtcNow;

        var nextVersion = _versions.Count == 0 ? 1 : _versions[^1].Version + 1;
        var processDefinitionId = GenerateProcessDefinitionId(processDefinitionKey, nextVersion, deployedAt);

        // `with` keeps every definition member (a field-by-field copy once dropped Escalations).
        var workflowWithId = workflow with { ProcessDefinitionId = processDefinitionId };
        IWorkflowDefinition scope = workflowWithId;

        var definition = new ProcessDefinition
        {
            ProcessDefinitionId = processDefinitionId,
            ProcessDefinitionKey = processDefinitionKey,
            Version = nextVersion,
            DeployedAt = deployedAt,
            Workflow = workflowWithId,
            BpmnXml = bpmnXml
        };

        // Preserve disabled state from previous version
        if (_versions.Count > 0)
        {
            definition.InheritStateFrom(_versions[^1]);
            if (!definition.IsActive)
                LogProcessRedeployedWhileDisabled(processDefinitionKey);
        }

        _byId[processDefinitionId] = definition;
        _versions.Add(definition);
        await _repository.SaveAsync(definition);

        LogDeployedWorkflow(processDefinitionKey, processDefinitionId, nextVersion);

        // Notify even when a registration call throws: the ones already written must reach the hosts.
        try
        {
            // Only register start event listeners if the process is active
            if (definition.IsActive)
                await RegisterAllStartEventListeners(workflowWithId, processDefinitionKey, processDefinitionId);

            // Unregister removed start event listeners from previous version
            if (_versions.Count > 1)
            {
                IWorkflowDefinition previousWorkflow = _versions[^2].Workflow;

                if (!scope.HasTimerStartEvent() && previousWorkflow.HasTimerStartEvent())
                {
                    var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
                    await scheduler.DeactivateScheduler();
                }

                var removedMessages = previousWorkflow.GetMessageStartEventNames()
                    .Except(scope.GetMessageStartEventNames());
                await UnregisterMessageListeners(removedMessages, processDefinitionKey);

                var removedSignals = previousWorkflow.GetSignalStartEventNames()
                    .Except(scope.GetSignalStartEventNames());
                await UnregisterSignalListeners(removedSignals, processDefinitionKey);

                var currentCondActivityIds = scope.GetConditionalStartEvents()
                    .Select(c => c.ActivityId).ToHashSet();
                var removedConditionals = previousWorkflow.GetConditionalStartEvents()
                    .Where(c => !currentCondActivityIds.Contains(c.ActivityId));
                await UnregisterConditionalListeners(removedConditionals, processDefinitionKey);
            }
        }
        finally
        {
            if (HasCachedStartEvents(scope) || (_versions.Count > 1 && HasCachedStartEvents(_versions[^2].Workflow)))
                await NotifyStartEventSubscriptionsChanged(processDefinitionKey);
        }

        return ToSummary(definition);
    }

    public Task<IWorkflowDefinition> GetLatestDefinition()
    {
        var key = this.GetPrimaryKeyString();
        if (_versions.Count == 0)
            throw new KeyNotFoundException($"Workflow with id '{key}' is not registered. Ensure the workflow is deployed before creating instances.");

        return Task.FromResult<IWorkflowDefinition>(_versions[^1].Workflow);
    }

    public Task<WorkflowDefinition> GetDefinitionById(string processDefinitionId)
    {
        if (!_byId.TryGetValue(processDefinitionId, out var definition))
            throw new KeyNotFoundException($"Process definition '{processDefinitionId}' not found.");

        return Task.FromResult(definition.Workflow);
    }

    public Task<IWorkflowDefinition?> GetStartableDefinition(string? processDefinitionId = null)
    {
        var key = this.GetPrimaryKeyString();
        var latest = GetLatestDefinitionOrThrow(key);

        var definition = latest;
        if (processDefinitionId is not null && !_byId.TryGetValue(processDefinitionId, out definition))
            throw new KeyNotFoundException($"Process definition with id '{processDefinitionId}' is not registered.");

        // Enabled/disabled is a property of the process key, held on its latest version.
        if (!latest.IsActive)
            return Task.FromResult<IWorkflowDefinition?>(null);

        return Task.FromResult<IWorkflowDefinition?>(definition.Workflow);
    }

    public Task<bool> IsActive()
    {
        if (_versions.Count == 0)
            return Task.FromResult(false);

        return Task.FromResult(_versions[^1].IsActive);
    }

    public async Task<ProcessDefinitionSummary> Disable()
    {
        var key = this.GetPrimaryKeyString();
        var definition = GetLatestDefinitionOrThrow(key);

        if (!definition.IsActive)
            return ToSummary(definition);

        definition.Disable();
        await _repository.UpdateAsync(definition);
        LogProcessDisabled(key);

        try
        {
            await UnregisterAllStartEventListeners(definition.Workflow, key);
        }
        finally
        {
            if (HasCachedStartEvents(definition.Workflow))
                await NotifyStartEventSubscriptionsChanged(key);
        }

        return ToSummary(definition);
    }

    public async Task<ProcessDefinitionSummary> Enable()
    {
        var key = this.GetPrimaryKeyString();
        var definition = GetLatestDefinitionOrThrow(key);

        if (definition.IsActive)
            return ToSummary(definition);

        definition.Enable();
        await _repository.UpdateAsync(definition);
        LogProcessEnabled(key);

        try
        {
            await RegisterAllStartEventListeners(definition.Workflow, key, definition.ProcessDefinitionId);
        }
        finally
        {
            if (HasCachedStartEvents(definition.Workflow))
                await NotifyStartEventSubscriptionsChanged(key);
        }

        return ToSummary(definition);
    }

    private ProcessDefinition GetLatestDefinitionOrThrow(string processDefinitionKey)
    {
        if (_versions.Count == 0)
            throw new KeyNotFoundException($"Workflow with id '{processDefinitionKey}' is not registered. Ensure the workflow is deployed before creating instances.");

        return _versions[^1];
    }

    private static ProcessDefinitionSummary ToSummary(ProcessDefinition definition) =>
        new(
            ProcessDefinitionId: definition.ProcessDefinitionId,
            ProcessDefinitionKey: definition.ProcessDefinitionKey,
            Version: definition.Version,
            DeployedAt: definition.DeployedAt,
            ActivitiesCount: definition.Workflow.Activities.Count,
            SequenceFlowsCount: definition.Workflow.SequenceFlows.Count,
            IsActive: definition.IsActive);

    private async Task UnregisterAllStartEventListeners(IWorkflowDefinition workflow, string processDefinitionKey)
    {
        if (workflow.HasTimerStartEvent())
        {
            var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
            await scheduler.DeactivateScheduler();
        }

        await UnregisterMessageListeners(workflow.GetMessageStartEventNames(), processDefinitionKey);
        await UnregisterSignalListeners(workflow.GetSignalStartEventNames(), processDefinitionKey);
        await UnregisterConditionalListeners(workflow.GetConditionalStartEvents(), processDefinitionKey);
    }

    private async Task RegisterAllStartEventListeners(IWorkflowDefinition workflow, string processDefinitionKey, string processDefinitionId)
    {
        if (workflow.HasTimerStartEvent())
        {
            var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
            await scheduler.ActivateScheduler(processDefinitionId);
        }

        await RegisterMessageListeners(workflow.GetMessageStartEventNames(), processDefinitionKey);
        await RegisterSignalListeners(workflow.GetSignalStartEventNames(), processDefinitionKey);
        await RegisterConditionalListeners(workflow.GetConditionalStartEvents(), processDefinitionKey);
    }

    private Task RegisterMessageListeners(IEnumerable<string> messageNames, string processDefinitionKey)
    {
        var tasks = messageNames.Select(name =>
            _grainFactory.GetGrain<IMessageStartEventListenerGrain>(name)
                .RegisterProcess(processDefinitionKey).AsTask());
        return Task.WhenAll(tasks);
    }

    private Task UnregisterMessageListeners(IEnumerable<string> messageNames, string processDefinitionKey)
    {
        var tasks = messageNames.Select(name =>
            _grainFactory.GetGrain<IMessageStartEventListenerGrain>(name)
                .UnregisterProcess(processDefinitionKey).AsTask());
        return Task.WhenAll(tasks);
    }

    private Task RegisterSignalListeners(IEnumerable<string> signalNames, string processDefinitionKey)
    {
        var tasks = signalNames.Select(name =>
            _grainFactory.GetGrain<ISignalStartEventListenerGrain>(name)
                .RegisterProcess(processDefinitionKey).AsTask());
        return Task.WhenAll(tasks);
    }

    private Task UnregisterSignalListeners(IEnumerable<string> signalNames, string processDefinitionKey)
    {
        var tasks = signalNames.Select(name =>
            _grainFactory.GetGrain<ISignalStartEventListenerGrain>(name)
                .UnregisterProcess(processDefinitionKey).AsTask());
        return Task.WhenAll(tasks);
    }

    private Task RegisterConditionalListeners(IEnumerable<ConditionalStartEvent> events, string processDefinitionKey)
    {
        var registry = _grainFactory.GetGrain<IConditionalStartEventRegistryGrain>(0);
        var tasks = events.Select(async cse =>
        {
            var grainKey = $"{processDefinitionKey}:{cse.ActivityId}";
            var listener = _grainFactory.GetGrain<IConditionalStartEventListenerGrain>(grainKey);
            await listener.Register(processDefinitionKey, cse.ActivityId, cse.ConditionExpression);
            await registry.Register(processDefinitionKey, cse.ActivityId, cse.ConditionExpression);
        });
        return Task.WhenAll(tasks);
    }

    private Task UnregisterConditionalListeners(IEnumerable<ConditionalStartEvent> events, string processDefinitionKey)
    {
        var registry = _grainFactory.GetGrain<IConditionalStartEventRegistryGrain>(0);
        var tasks = events.Select(async cse =>
        {
            var grainKey = $"{processDefinitionKey}:{cse.ActivityId}";
            var listener = _grainFactory.GetGrain<IConditionalStartEventListenerGrain>(grainKey);
            await listener.Unregister();
            await registry.Unregister(processDefinitionKey, cse.ActivityId);
        });
        return Task.WhenAll(tasks);
    }

    /// <summary>Whether <paramref name="workflow"/> has a start event every host caches the subscribers of (#858).</summary>
    private static bool HasCachedStartEvents(IWorkflowDefinition workflow) =>
        workflow.GetMessageStartEventNames().Count > 0
        || workflow.GetSignalStartEventNames().Count > 0
        || workflow.GetConditionalStartEvents().Any();

    /// <summary>
    /// Refreshes every host's <see cref="StartEventSubscriptionCache"/> before the deployment call
    /// returns. Log-only on failure: the registrations are already persisted, and a host that misses
    /// the change reloads within <see cref="StartEventSubscriptionCache.EntryLifetime"/>.
    /// </summary>
    private async Task NotifyStartEventSubscriptionsChanged(string processDefinitionKey)
    {
        try
        {
            await _grainFactory.GetGrain<IStartEventSubscriptionNotifierGrain>(0).NotifyChanged();
        }
        catch (Exception ex)
        {
            LogStartEventSubscriptionNotifyFailed(processDefinitionKey, ex);
        }
    }

    private string GenerateProcessDefinitionId(string key, int version, DateTimeOffset deployedAt)
    {
        var ts = deployedAt.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'.'fffffff'Z'");
        var baseId = $"{key}:{version}:{ts}";

        var candidate = baseId;
        var i = 1;
        while (_byId.ContainsKey(candidate))
        {
            candidate = $"{baseId}-{i++}";
        }

        return candidate;
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Deployed workflow {WorkflowKey} as {ProcessDefinitionId} version {Version}")]
    private partial void LogDeployedWorkflow(string workflowKey, string processDefinitionId, int version);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Process {ProcessDefinitionKey} disabled")]
    private partial void LogProcessDisabled(string processDefinitionKey);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "Process {ProcessDefinitionKey} enabled")]
    private partial void LogProcessEnabled(string processDefinitionKey);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Process {ProcessDefinitionKey} redeployed while disabled — new version remains disabled")]
    private partial void LogProcessRedeployedWhileDisabled(string processDefinitionKey);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Process {ProcessDefinitionKey}: could not tell the hosts that its start events changed; their caches refresh on expiry")]
    private partial void LogStartEventSubscriptionNotifyFailed(string processDefinitionKey, Exception ex);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Information, Message = "ProcessDefinitionGrain activated for key '{ProcessDefinitionKey}' with {VersionCount} version(s)")]
    private partial void LogActivated(string processDefinitionKey, int versionCount);
}
