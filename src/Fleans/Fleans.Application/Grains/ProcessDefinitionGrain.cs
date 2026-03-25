using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
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

        var versions = await _repository.GetByKeyAsync(this.GetPrimaryKeyString());
        foreach (var def in versions)
        {
            _byId[def.ProcessDefinitionId] = def;
            _versions.Add(def);
        }
    }

    public async Task<IWorkflowInstanceGrain> CreateInstance()
    {
        var processDefinitionKey = this.GetPrimaryKeyString();
        var definition = GetLatestDefinitionOrThrow();

        var guid = Guid.NewGuid();
        LogCreatingInstance(processDefinitionKey, definition.ProcessDefinitionId, guid);

        RequestContext.Set("WorkflowId", processDefinitionKey);
        RequestContext.Set("ProcessDefinitionId", definition.ProcessDefinitionId);
        RequestContext.Set("WorkflowInstanceId", guid.ToString());

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

        var guid = Guid.NewGuid();
        LogCreatingInstance(definition.ProcessDefinitionKey, processDefinitionId, guid);

        RequestContext.Set("WorkflowId", definition.ProcessDefinitionKey);
        RequestContext.Set("ProcessDefinitionId", processDefinitionId);
        RequestContext.Set("WorkflowInstanceId", guid.ToString());

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

        var deployedAt = DateTimeOffset.UtcNow;
        var processDefinitionKey = this.GetPrimaryKeyString();

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

        LogDeployedWorkflow(processDefinitionKey, processDefinitionId, nextVersion);

        // Register with the registry on first deploy for this key
        if (_versions.Count == 1)
        {
            var registry = _grainFactory.GetGrain<IProcessDefinitionRegistryGrain>(0);
            await registry.RegisterKey(processDefinitionKey);
        }

        // Only register start event listeners if the process is active
        if (definition.IsActive)
            await RegisterAllStartEventListeners(workflowWithId, processDefinitionKey, processDefinitionId);

        // Unregister removed start event listeners from previous version
        if (_versions.Count > 1)
        {
            IWorkflowDefinition previousWorkflow = _versions[^2].Workflow;

            // Timer: if previous had timer but new doesn't, deactivate
            if (!scope.HasTimerStartEvent() && previousWorkflow.HasTimerStartEvent())
            {
                var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
                await scheduler.DeactivateScheduler();
            }

            // Messages: unregister removed message names
            var newMessageNames = scope.GetMessageStartEventNames();
            foreach (var removedName in previousWorkflow.GetMessageStartEventNames().Except(newMessageNames))
            {
                var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(removedName);
                await listener.UnregisterProcess(processDefinitionKey);
            }

            // Signals: unregister removed signal names
            var newSignalNames = scope.GetSignalStartEventNames();
            foreach (var removedName in previousWorkflow.GetSignalStartEventNames().Except(newSignalNames))
            {
                var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(removedName);
                await listener.UnregisterProcess(processDefinitionKey);
            }
        }

        return ToSummary(definition);
    }

    public Task<IWorkflowDefinition> GetLatestDefinition()
    {
        var definition = GetLatestDefinitionOrThrow();
        return Task.FromResult<IWorkflowDefinition>(definition.Workflow);
    }

    public Task<WorkflowDefinition> GetDefinitionById(string processDefinitionId)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
            throw new ArgumentException("ProcessDefinitionId cannot be null or empty.", nameof(processDefinitionId));

        if (!_byId.TryGetValue(processDefinitionId, out var definition))
        {
            LogDefinitionNotFound(processDefinitionId);
            throw new KeyNotFoundException($"Process definition '{processDefinitionId}' not found.");
        }

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
        var definition = GetLatestDefinitionOrThrow(allowDisabled: true);

        if (!definition.IsActive)
            return ToSummary(definition);

        var processDefinitionKey = this.GetPrimaryKeyString();
        definition.Disable();
        await _repository.UpdateAsync(definition);
        LogProcessDisabled(processDefinitionKey);

        await UnregisterAllStartEventListeners(definition.Workflow, processDefinitionKey);

        return ToSummary(definition);
    }

    /// <remarks>
    /// Known edge case: if the silo crashes after SaveAsync but before RegisterAllStartEventListeners
    /// completes, the process will be marked active but listeners won't be registered. Start events
    /// won't fire until the next redeployment or manual re-enable.
    /// </remarks>
    public async Task<ProcessDefinitionSummary> Enable()
    {
        var definition = GetLatestDefinitionOrThrow(allowDisabled: true);

        if (definition.IsActive)
            return ToSummary(definition);

        var processDefinitionKey = this.GetPrimaryKeyString();
        definition.Enable();
        await _repository.UpdateAsync(definition);
        LogProcessEnabled(processDefinitionKey);

        await RegisterAllStartEventListeners(definition.Workflow, processDefinitionKey, definition.ProcessDefinitionId);

        return ToSummary(definition);
    }

    private ProcessDefinition GetLatestDefinitionOrThrow(bool allowDisabled = false)
    {
        var processDefinitionKey = this.GetPrimaryKeyString();

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
        if (workflow.Activities.OfType<TimerStartEvent>().Any())
        {
            var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
            await scheduler.DeactivateScheduler();
        }

        foreach (var messageStart in workflow.Activities.OfType<MessageStartEvent>())
        {
            var msgDef = workflow.Messages.FirstOrDefault(m => m.Id == messageStart.MessageDefinitionId);
            if (msgDef != null)
            {
                var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(msgDef.Name);
                await listener.UnregisterProcess(processDefinitionKey);
            }
        }

        foreach (var signalStart in workflow.Activities.OfType<SignalStartEvent>())
        {
            var sigDef = workflow.Signals.FirstOrDefault(s => s.Id == signalStart.SignalDefinitionId);
            if (sigDef != null)
            {
                var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(sigDef.Name);
                await listener.UnregisterProcess(processDefinitionKey);
            }
        }
    }

    private async Task RegisterAllStartEventListeners(IWorkflowDefinition workflow, string processDefinitionKey, string processDefinitionId)
    {
        if (workflow.Activities.OfType<TimerStartEvent>().Any())
        {
            var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
            await scheduler.ActivateScheduler(processDefinitionId);
        }

        foreach (var messageStart in workflow.Activities.OfType<MessageStartEvent>())
        {
            var msgDef = workflow.Messages.FirstOrDefault(m => m.Id == messageStart.MessageDefinitionId);
            if (msgDef != null)
            {
                var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(msgDef.Name);
                await listener.RegisterProcess(processDefinitionKey);
            }
        }

        foreach (var signalStart in workflow.Activities.OfType<SignalStartEvent>())
        {
            var sigDef = workflow.Signals.FirstOrDefault(s => s.Id == signalStart.SignalDefinitionId);
            if (sigDef != null)
            {
                var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(sigDef.Name);
                await listener.RegisterProcess(processDefinitionKey);
            }
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

    [LoggerMessage(EventId = 7000, Level = LogLevel.Warning, Message = "Process definition '{ProcessDefinitionId}' not found in storage")]
    private partial void LogDefinitionNotFound(string processDefinitionId);
}
