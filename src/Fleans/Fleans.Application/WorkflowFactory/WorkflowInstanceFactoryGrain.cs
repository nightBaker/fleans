using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Application.WorkflowFactory;

[Reentrant]
public partial class WorkflowInstanceFactoryGrain : Grain, IWorkflowInstanceFactoryGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstanceFactoryGrain> _logger;
    private readonly IProcessDefinitionRepository _repository;
    // Camunda-like process definitions:
    // - BPMN <process id> is the key
    // - each deploy creates a new immutable version per key
    // - "start by key" uses latest version
    private readonly Dictionary<string, ProcessDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ProcessDefinition>> _byKey = new(StringComparer.Ordinal);

    public WorkflowInstanceFactoryGrain(
        IGrainFactory grainFactory,
        ILogger<WorkflowInstanceFactoryGrain> logger,
        IProcessDefinitionRepository repository)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _repository = repository;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        var all = await _repository.GetAllAsync();
        foreach (var def in all)
        {
            _byId[def.ProcessDefinitionId] = def;
            if (!_byKey.TryGetValue(def.ProcessDefinitionKey, out var versions))
            {
                versions = new List<ProcessDefinition>();
                _byKey[def.ProcessDefinitionKey] = versions;
            }
            versions.Add(def);
        }
    }

    public async Task<IWorkflowInstanceGrain> CreateWorkflowInstanceGrain(string workflowId)
    {
        // Back-compat: treat workflowId as process definition key and start the latest version.
        var definition = GetLatestDefinitionOrThrow(workflowId);

        var guid = Guid.NewGuid();
        LogCreatingInstance(workflowId, definition.ProcessDefinitionId, guid);

        RequestContext.Set("WorkflowId", workflowId);
        RequestContext.Set("ProcessDefinitionId", definition.ProcessDefinitionId);
        RequestContext.Set("WorkflowInstanceId", guid.ToString());

        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(guid);

        await workflowInstanceGrain.SetWorkflow(definition.Workflow);

        return workflowInstanceGrain;
    }

    public async Task<IWorkflowInstanceGrain> CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            throw new ArgumentException("ProcessDefinitionId cannot be null or empty.", nameof(processDefinitionId));
        }

        if (!_byId.TryGetValue(processDefinitionId, out var definition))
        {
            throw new KeyNotFoundException($"Process definition with id '{processDefinitionId}' is not registered.");
        }

        var guid = Guid.NewGuid();
        LogCreatingInstance(definition.ProcessDefinitionKey, processDefinitionId, guid);

        RequestContext.Set("WorkflowId", definition.ProcessDefinitionKey);
        RequestContext.Set("ProcessDefinitionId", processDefinitionId);
        RequestContext.Set("WorkflowInstanceId", guid.ToString());

        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(guid);

        await workflowInstanceGrain.SetWorkflow(definition.Workflow);

        return workflowInstanceGrain;
    }

    public async Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
    {
        if (workflow == null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
        {
            throw new ArgumentException("WorkflowId cannot be null or empty.", nameof(workflow));
        }

        var deployedAt = DateTimeOffset.UtcNow;
        var processDefinitionKey = workflow.WorkflowId;

        if (!_byKey.TryGetValue(processDefinitionKey, out var versions))
        {
            versions = new List<ProcessDefinition>();
            _byKey[processDefinitionKey] = versions;
        }

        var nextVersion = versions.Count == 0 ? 1 : versions[^1].Version + 1;
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
        if (versions.Count > 0)
        {
            definition.InheritStateFrom(versions[^1]);
            if (!definition.IsActive)
            {
                LogProcessRedeployedWhileDisabled(processDefinitionKey);
            }
        }

        _byId[processDefinitionId] = definition;
        versions.Add(definition);
        await _repository.SaveAsync(definition);

        LogDeployedWorkflow(processDefinitionKey, processDefinitionId, nextVersion);

        // Only register start event listeners if the process is active
        if (definition.IsActive)
        {
            await RegisterAllStartEventListeners(workflowWithId, processDefinitionKey, processDefinitionId);
        }

        // Unregister removed start event listeners from previous version
        if (versions.Count > 1)
        {
            IWorkflowDefinition previousWorkflow = versions[^2].Workflow;

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

    public Task<IWorkflowDefinition> GetLatestWorkflowDefinition(string processDefinitionKey)
    {
        var definition = GetLatestDefinitionOrThrow(processDefinitionKey);
        return Task.FromResult<IWorkflowDefinition>(definition.Workflow);
    }

    public Task<bool> IsProcessActive(string processDefinitionKey)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionKey)
            || !_byKey.TryGetValue(processDefinitionKey, out var versions)
            || versions.Count == 0)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(versions[^1].IsActive);
    }

    public async Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey)
    {
        var definition = GetLatestDefinitionOrThrow(processDefinitionKey, allowDisabled: true);

        if (!definition.IsActive)
            return ToSummary(definition);

        definition.Disable();
        await _repository.UpdateAsync(definition);
        LogProcessDisabled(processDefinitionKey);

        await UnregisterAllStartEventListeners(definition.Workflow, processDefinitionKey);

        return ToSummary(definition);
    }

    /// <remarks>
    /// Known edge case: if the silo crashes after SaveAsync but before RegisterAllStartEventListeners
    /// completes, the process will be marked active but listeners won't be registered. Start events
    /// won't fire until the next redeployment or manual re-enable. DisableProcess does not have this
    /// issue because the IsProcessActive guard in listener grains prevents new instances even if
    /// listener unregistration hasn't completed yet.
    /// </remarks>
    public async Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey)
    {
        var definition = GetLatestDefinitionOrThrow(processDefinitionKey, allowDisabled: true);

        if (definition.IsActive)
            return ToSummary(definition);

        definition.Enable();
        await _repository.UpdateAsync(definition);
        LogProcessEnabled(processDefinitionKey);

        await RegisterAllStartEventListeners(definition.Workflow, processDefinitionKey, definition.ProcessDefinitionId);

        return ToSummary(definition);
    }

    private ProcessDefinition GetLatestDefinitionOrThrow(string processDefinitionKey, bool allowDisabled = false)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionKey))
        {
            throw new ArgumentException("ProcessDefinitionKey cannot be null or empty.", nameof(processDefinitionKey));
        }

        if (!_byKey.TryGetValue(processDefinitionKey, out var versions) || versions.Count == 0)
        {
            throw new KeyNotFoundException($"Workflow with id '{processDefinitionKey}' is not registered. Ensure the workflow is deployed before creating instances.");
        }

        var definition = versions[^1];

        if (!allowDisabled && !definition.IsActive)
        {
            throw new InvalidOperationException($"Process '{processDefinitionKey}' is disabled. Enable it before creating new instances.");
        }

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
        // Use high-resolution UTC timestamp; append a suffix if a collision occurs within the same tick.
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
