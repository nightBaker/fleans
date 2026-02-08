using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Application.WorkflowFactory;

[Reentrant]
public partial class WorkflowInstanceFactoryGrain : Grain, IWorkflowInstanceFactoryGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstanceFactoryGrain> _logger;
    // Camunda-like process definitions:
    // - BPMN <process id> is the key
    // - each deploy creates a new immutable version per key
    // - "start by key" uses latest version
    private readonly Dictionary<string, ProcessDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ProcessDefinition>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Guid>> _instancesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _instanceToDefinitionId = new();

    public WorkflowInstanceFactoryGrain(IGrainFactory grainFactory, ILogger<WorkflowInstanceFactoryGrain> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }
    
    public async Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId)
    {
        // Back-compat: treat workflowId as process definition key and start the latest version.
        var definition = GetLatestDefinitionOrThrow(workflowId);

        var guid = Guid.NewGuid();
        LogCreatingInstance(workflowId, definition.ProcessDefinitionId, guid);

        RequestContext.Set("WorkflowId", workflowId);
        RequestContext.Set("ProcessDefinitionId", definition.ProcessDefinitionId);
        RequestContext.Set("WorkflowInstanceId", guid.ToString());

        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstance>(guid);

        await workflowInstanceGrain.SetWorkflow(definition.Workflow);
        TrackInstance(definition.ProcessDefinitionKey, guid, definition.ProcessDefinitionId);

        return workflowInstanceGrain;
    }

    public async Task<IWorkflowInstance> CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId)
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

        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstance>(guid);

        await workflowInstanceGrain.SetWorkflow(definition.Workflow);
        TrackInstance(definition.ProcessDefinitionKey, guid, definition.ProcessDefinitionId);

        return workflowInstanceGrain;
    }

    public Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
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

        var workflowWithId = workflow with { ProcessDefinitionId = processDefinitionId };

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

        LogDeployedWorkflow(processDefinitionKey, processDefinitionId, nextVersion);

        return Task.FromResult(ToSummary(definition));
    }

    public async Task RegisterWorkflow(IWorkflowDefinition workflow)
    {
        LogRegisteringWorkflow(workflow?.WorkflowId ?? "null");
        // Back-compat: old API rejected duplicates; keep that behavior by only allowing the first version.
        if (workflow == null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
        {
            throw new ArgumentException("WorkflowId cannot be null or empty.", nameof(workflow));
        }

        if (_byKey.ContainsKey(workflow.WorkflowId))
        {
            throw new InvalidOperationException($"Workflow with id '{workflow.WorkflowId}' is already registered.");
        }

        var def = new WorkflowDefinition
        {
            WorkflowId = workflow.WorkflowId,
            Activities = workflow.Activities,
            SequenceFlows = workflow.SequenceFlows
        };

        await DeployWorkflow(def, string.Empty);
    }

    public Task<IReadOnlyList<IWorkflowDefinition>> GetAllWorkflows()
    {
        // Back-compat: return latest workflow per key.
        var latest = _byKey.Values
            .Where(v => v.Count > 0)
            .Select(v => (IWorkflowDefinition)v[^1].Workflow)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<IWorkflowDefinition>>(latest);
    }

    public Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions()
    {
        var all = _byKey.Values
            .SelectMany(v => v)
            .OrderBy(d => d.ProcessDefinitionKey, StringComparer.Ordinal)
            .ThenBy(d => d.Version)
            .Select(ToSummary)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<ProcessDefinitionSummary>>(all);
    }

    public Task<bool> IsWorkflowRegistered(string workflowId)
    {
        // Back-compat: treat as key.
        return Task.FromResult(_byKey.ContainsKey(workflowId));
    }

    private void TrackInstance(string processDefinitionKey, Guid instanceId, string processDefinitionId)
    {
        if (!_instancesByKey.TryGetValue(processDefinitionKey, out var instances))
        {
            instances = new List<Guid>();
            _instancesByKey[processDefinitionKey] = instances;
        }
        instances.Add(instanceId);
        _instanceToDefinitionId[instanceId] = processDefinitionId;
    }

    public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey)
    {
        if (!_instancesByKey.TryGetValue(processDefinitionKey, out var instanceIds))
            return Array.Empty<WorkflowInstanceInfo>();

        var result = new List<WorkflowInstanceInfo>();
        foreach (var id in instanceIds)
        {
            var instance = _grainFactory.GetGrain<IWorkflowInstance>(id);
            var state = await instance.GetState();
            var isStarted = await state.IsStarted();
            var isCompleted = await state.IsCompleted();
            var defId = _instanceToDefinitionId[id];
            result.Add(new WorkflowInstanceInfo(id, defId, isStarted, isCompleted));
        }
        return result;
    }

    public Task<string> GetBpmnXml(string processDefinitionId)
    {
        if (!_byId.TryGetValue(processDefinitionId, out var definition))
            throw new KeyNotFoundException($"Process definition '{processDefinitionId}' not found.");
        return Task.FromResult(definition.BpmnXml);
    }

    public Task<string> GetBpmnXmlByKey(string processDefinitionKey)
    {
        var definition = GetLatestDefinitionOrThrow(processDefinitionKey);
        return Task.FromResult(definition.BpmnXml);
    }

    public Task<string> GetBpmnXmlByInstanceId(Guid instanceId)
    {
        if (!_instanceToDefinitionId.TryGetValue(instanceId, out var definitionId))
            throw new KeyNotFoundException($"Instance '{instanceId}' not found.");
        return GetBpmnXml(definitionId);
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

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Registering workflow {WorkflowId}")]
    private partial void LogRegisteringWorkflow(string workflowId);
}
