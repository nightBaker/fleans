using Fleans.Application.Logging;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Persistence;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[Reentrant]
public partial class ProcessDefinitionGrain : Grain, IProcessDefinitionGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ProcessDefinitionGrain> _logger;
    private readonly IProcessDefinitionRepository _repository;

    private readonly Dictionary<string, ProcessDefinition> _byId = new(StringComparer.Ordinal);
    private readonly List<ProcessDefinition> _versions = [];

    public ProcessDefinitionGrain(
        IGrainFactory grainFactory,
        ILogger<ProcessDefinitionGrain> logger,
        IProcessDefinitionRepository repository)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _repository = repository;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        var key = this.GetPrimaryKeyString();
        var definitions = await _repository.GetByKeyAsync(key);
        foreach (var def in definitions)
        {
            _byId[def.ProcessDefinitionId] = def;
            _versions.Add(def);
        }
    }

    public async Task<IWorkflowInstanceGrain> CreateInstance()
    {
        var key = this.GetPrimaryKeyString();
        var definition = GetLatestDefinitionOrThrow(key);

        var guid = Guid.NewGuid();
        LogCreatingInstance(key, definition.ProcessDefinitionId, guid);

        RequestContext.Set(WorkflowContextKeys.WorkflowId, key);
        RequestContext.Set(WorkflowContextKeys.ProcessDefinitionId, definition.ProcessDefinitionId);
        RequestContext.Set(WorkflowContextKeys.WorkflowInstanceId, guid.ToString());

        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(guid);
        await workflowInstanceGrain.SetWorkflow(definition.Workflow);

        return workflowInstanceGrain;
    }

    public async Task<IWorkflowInstanceGrain> CreateInstanceByDefinitionId(string processDefinitionId)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
            throw new ArgumentException("ProcessDefinitionId cannot be null or empty.", nameof(processDefinitionId));

        if (!_byId.TryGetValue(processDefinitionId, out var definition))
            throw new KeyNotFoundException($"Process definition with id '{processDefinitionId}' is not registered.");

        var key = this.GetPrimaryKeyString();
        var guid = Guid.NewGuid();
        LogCreatingInstance(key, processDefinitionId, guid);

        RequestContext.Set(WorkflowContextKeys.WorkflowId, key);
        RequestContext.Set(WorkflowContextKeys.ProcessDefinitionId, processDefinitionId);
        RequestContext.Set(WorkflowContextKeys.WorkflowInstanceId, guid.ToString());

        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(guid);
        await workflowInstanceGrain.SetWorkflow(definition.Workflow);

        return workflowInstanceGrain;
    }

    public async Task<ProcessDefinitionSummary> DeployVersion(WorkflowDefinition workflow, string bpmnXml)
    {
        if (workflow == null)
            throw new ArgumentNullException(nameof(workflow));

        if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
            throw new ArgumentException("WorkflowId cannot be null or empty.", nameof(workflow));

        var processDefinitionKey = this.GetPrimaryKeyString();
        var deployedAt = DateTimeOffset.UtcNow;

        var nextVersion = _versions.Count == 0 ? 1 : _versions[^1].Version + 1;
        var processDefinitionId = GenerateProcessDefinitionId(processDefinitionKey, nextVersion, deployedAt);

        var workflowWithId = new WorkflowDefinition
        {
            WorkflowId = workflow.WorkflowId,
            Activities = workflow.Activities,
            SequenceFlows = workflow.SequenceFlows,
            Messages = workflow.Messages,
            Signals = workflow.Signals,
            ProcessDefinitionId = processDefinitionId
        };
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

        // Register key in the registry (idempotent)
        var registry = _grainFactory.GetGrain<IProcessDefinitionRegistryGrain>(0);
        await registry.RegisterKey(processDefinitionKey);

        LogDeployedWorkflow(processDefinitionKey, processDefinitionId, nextVersion);

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

    public Task<bool> IsActive()
    {
        if (_versions.Count == 0)
            return Task.FromResult(false);

        return Task.FromResult(_versions[^1].IsActive);
    }

    public async Task<ProcessDefinitionSummary> Disable()
    {
        var key = this.GetPrimaryKeyString();
        var definition = GetLatestDefinitionOrThrow(key, allowDisabled: true);

        if (!definition.IsActive)
            return ToSummary(definition);

        definition.Disable();
        await _repository.UpdateAsync(definition);
        LogProcessDisabled(key);

        await UnregisterAllStartEventListeners(definition.Workflow, key);

        return ToSummary(definition);
    }

    public async Task<ProcessDefinitionSummary> Enable()
    {
        var key = this.GetPrimaryKeyString();
        var definition = GetLatestDefinitionOrThrow(key, allowDisabled: true);

        if (definition.IsActive)
            return ToSummary(definition);

        definition.Enable();
        await _repository.UpdateAsync(definition);
        LogProcessEnabled(key);

        await RegisterAllStartEventListeners(definition.Workflow, key, definition.ProcessDefinitionId);

        return ToSummary(definition);
    }

    private ProcessDefinition GetLatestDefinitionOrThrow(string processDefinitionKey, bool allowDisabled = false)
    {
        if (_versions.Count == 0)
            throw new KeyNotFoundException($"Workflow with id '{processDefinitionKey}' is not registered. Ensure the workflow is deployed before creating instances.");

        var definition = _versions[^1];

        if (!allowDisabled && !definition.IsActive)
            throw new InvalidOperationException($"Process '{processDefinitionKey}' is disabled. Enable it before creating new instances.");

        return definition;
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
    }

    private async Task RegisterMessageListeners(IEnumerable<string> messageNames, string processDefinitionKey)
    {
        foreach (var name in messageNames)
        {
            var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(name);
            await listener.RegisterProcess(processDefinitionKey);
        }
    }

    private async Task UnregisterMessageListeners(IEnumerable<string> messageNames, string processDefinitionKey)
    {
        foreach (var name in messageNames)
        {
            var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(name);
            await listener.UnregisterProcess(processDefinitionKey);
        }
    }

    private async Task RegisterSignalListeners(IEnumerable<string> signalNames, string processDefinitionKey)
    {
        foreach (var name in signalNames)
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(name);
            await listener.RegisterProcess(processDefinitionKey);
        }
    }

    private async Task UnregisterSignalListeners(IEnumerable<string> signalNames, string processDefinitionKey)
    {
        foreach (var name in signalNames)
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(name);
            await listener.UnregisterProcess(processDefinitionKey);
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

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Creating workflow instance for {WorkflowKey} definition {ProcessDefinitionId}, instance {InstanceId}")]
    private partial void LogCreatingInstance(string workflowKey, string processDefinitionId, Guid instanceId);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Process {ProcessDefinitionKey} disabled")]
    private partial void LogProcessDisabled(string processDefinitionKey);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "Process {ProcessDefinitionKey} enabled")]
    private partial void LogProcessEnabled(string processDefinitionKey);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Process {ProcessDefinitionKey} redeployed while disabled — new version remains disabled")]
    private partial void LogProcessRedeployedWhileDisabled(string processDefinitionKey);
}
