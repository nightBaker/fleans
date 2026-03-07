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

        var definition = new ProcessDefinition
        {
            ProcessDefinitionId = processDefinitionId,
            ProcessDefinitionKey = processDefinitionKey,
            Version = nextVersion,
            DeployedAt = deployedAt,
            Workflow = workflowWithId,
            BpmnXml = bpmnXml
        };

        _byId[processDefinitionId] = definition;
        versions.Add(definition);
        await _repository.SaveAsync(definition);

        LogDeployedWorkflow(processDefinitionKey, processDefinitionId, nextVersion);

        // Activate timer scheduler if the workflow contains a TimerStartEvent
        if (workflowWithId.Activities.OfType<TimerStartEvent>().Any())
        {
            var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
            await scheduler.ActivateScheduler(processDefinitionId);
        }

        // Register/unregister message start event listeners on redeployment
        var newMessageNames = new HashSet<string>();
        foreach (var messageStart in workflowWithId.Activities.OfType<MessageStartEvent>())
        {
            var messageDefinition = workflowWithId.Messages.FirstOrDefault(m => m.Id == messageStart.MessageDefinitionId);
            if (messageDefinition != null)
            {
                newMessageNames.Add(messageDefinition.Name);
                var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(messageDefinition.Name);
                await listener.RegisterProcess(processDefinitionKey);
            }
        }

        // Unregister previous version's message start events that are no longer present
        if (versions.Count > 1)
        {
            var previousWorkflow = versions[^2].Workflow;
            foreach (var oldMessageStart in previousWorkflow.Activities.OfType<MessageStartEvent>())
            {
                var oldMessageDef = previousWorkflow.Messages.FirstOrDefault(m => m.Id == oldMessageStart.MessageDefinitionId);
                if (oldMessageDef != null && !newMessageNames.Contains(oldMessageDef.Name))
                {
                    var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(oldMessageDef.Name);
                    await listener.UnregisterProcess(processDefinitionKey);
                }
            }
        }

        // Register/unregister signal start event listeners
        var newSignalNames = new HashSet<string>();
        foreach (var signalStart in workflowWithId.Activities.OfType<SignalStartEvent>())
        {
            var signalDefinition = workflowWithId.Signals.FirstOrDefault(s => s.Id == signalStart.SignalDefinitionId);
            if (signalDefinition != null)
            {
                newSignalNames.Add(signalDefinition.Name);
                var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(signalDefinition.Name);
                await listener.RegisterProcess(processDefinitionKey);
            }
        }

        // Unregister previous version's signal start events that are no longer present
        if (versions.Count > 1)
        {
            var previousWorkflow = versions[^2].Workflow;
            foreach (var oldSignalStart in previousWorkflow.Activities.OfType<SignalStartEvent>())
            {
                var oldSignalDef = previousWorkflow.Signals.FirstOrDefault(s => s.Id == oldSignalStart.SignalDefinitionId);
                if (oldSignalDef != null && !newSignalNames.Contains(oldSignalDef.Name))
                {
                    var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(oldSignalDef.Name);
                    await listener.UnregisterProcess(processDefinitionKey);
                }
            }
        }

        return ToSummary(definition);
    }

    public Task<IWorkflowDefinition> GetLatestWorkflowDefinition(string processDefinitionKey)
    {
        var definition = GetLatestDefinitionOrThrow(processDefinitionKey);
        return Task.FromResult<IWorkflowDefinition>(definition.Workflow);
    }

    private ProcessDefinition GetLatestDefinitionOrThrow(string processDefinitionKey)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionKey))
        {
            throw new ArgumentException("ProcessDefinitionKey cannot be null or empty.", nameof(processDefinitionKey));
        }

        if (!_byKey.TryGetValue(processDefinitionKey, out var versions) || versions.Count == 0)
        {
            throw new KeyNotFoundException($"Workflow with id '{processDefinitionKey}' is not registered. Ensure the workflow is deployed before creating instances.");
        }

        return versions[^1];
    }

    private static ProcessDefinitionSummary ToSummary(ProcessDefinition definition) =>
        new(
            ProcessDefinitionId: definition.ProcessDefinitionId,
            ProcessDefinitionKey: definition.ProcessDefinitionKey,
            Version: definition.Version,
            DeployedAt: definition.DeployedAt,
            ActivitiesCount: definition.Workflow.Activities.Count,
            SequenceFlowsCount: definition.Workflow.SequenceFlows.Count);

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
}
