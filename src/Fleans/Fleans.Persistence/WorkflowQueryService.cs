using System.Dynamic;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Fleans.Persistence;

public class WorkflowQueryService : IWorkflowQueryService
{
    private readonly IDbContextFactory<FleanDbContext> _dbContextFactory;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        PreserveReferencesHandling = PreserveReferencesHandling.Objects,
        SerializationBinder = new DomainAssemblySerializationBinder()
    };

    public WorkflowQueryService(IDbContextFactory<FleanDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var state = await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .Include(s => s.ConditionSequenceStates)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == workflowInstanceId);

        if (state is null)
            return null;

        // Load activity instance states for all entries
        var activityInstanceIds = state.Entries.Select(e => e.ActivityInstanceId).ToList();
        var activityStates = await db.ActivityInstances
            .AsNoTracking()
            .Where(a => activityInstanceIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);

        // Load process definition for condition enrichment
        WorkflowDefinition? workflowDef = null;
        if (state.ProcessDefinitionId is not null)
        {
            var processDef = await db.ProcessDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProcessDefinitionId == state.ProcessDefinitionId);
            workflowDef = processDef?.Workflow;
        }

        var activeEntries = state.Entries.Where(e => !e.IsCompleted).ToList();
        var completedEntries = state.Entries.Where(e => e.IsCompleted).ToList();

        var activeSnapshots = activeEntries
            .Select(e => ToActivitySnapshot(e, activityStates))
            .ToList();
        var completedSnapshots = completedEntries
            .Select(e => ToActivitySnapshot(e, activityStates))
            .ToList();

        var activeIds = activeEntries.Select(e => e.ActivityId).ToList();
        var completedIds = completedEntries.Select(e => e.ActivityId).ToList();

        var variableStates = state.VariableStates.Select(vs =>
        {
            var dict = ((IDictionary<string, object>)vs.Variables)
                .ToDictionary(e => e.Key, e => e.Value?.ToString() ?? "");
            return new VariableStateSnapshot(vs.Id, dict);
        }).ToList();

        var conditionSequences = state.ConditionSequenceStates
            .Select(cs => ToConditionSnapshot(cs, workflowDef))
            .ToList();

        return new InstanceStateSnapshot(
            activeIds, completedIds, state.IsStarted, state.IsCompleted,
            activeSnapshots, completedSnapshots,
            variableStates, conditionSequences,
            state.ProcessDefinitionId,
            state.CreatedAt, state.ExecutionStartedAt, state.CompletedAt);
    }

    public async Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitions = await db.ProcessDefinitions
            .AsNoTracking()
            .OrderBy(d => d.ProcessDefinitionKey)
            .ThenBy(d => d.Version)
            .ToListAsync();

        return definitions.Select(d => new ProcessDefinitionSummary(
            d.ProcessDefinitionId,
            d.ProcessDefinitionKey,
            d.Version,
            d.DeployedAt,
            d.Workflow.Activities.Count,
            d.Workflow.SequenceFlows.Count)).ToList();
    }

    public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionIds = await db.ProcessDefinitions
            .AsNoTracking()
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .Select(p => p.ProcessDefinitionId)
            .ToListAsync();

        var instances = await db.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.ProcessDefinitionId != null && definitionIds.Contains(w.ProcessDefinitionId))
            .ToListAsync();

        return instances.Select(w => new WorkflowInstanceInfo(
            w.Id, w.ProcessDefinitionId ?? "", w.IsStarted, w.IsCompleted,
            w.CreatedAt, w.ExecutionStartedAt, w.CompletedAt)).ToList();
    }

    public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string key, int version)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionId = await db.ProcessDefinitions
            .AsNoTracking()
            .Where(p => p.ProcessDefinitionKey == key && p.Version == version)
            .Select(p => p.ProcessDefinitionId)
            .FirstOrDefaultAsync();

        if (definitionId is null)
            return [];

        var instances = await db.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.ProcessDefinitionId == definitionId)
            .ToListAsync();

        return instances.Select(w => new WorkflowInstanceInfo(
            w.Id, w.ProcessDefinitionId ?? "", w.IsStarted, w.IsCompleted,
            w.CreatedAt, w.ExecutionStartedAt, w.CompletedAt)).ToList();
    }

    public async Task<string?> GetBpmnXml(Guid instanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var processDefinitionId = await db.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.Id == instanceId)
            .Select(w => w.ProcessDefinitionId)
            .FirstOrDefaultAsync();

        if (processDefinitionId is null)
            return null;

        return await db.ProcessDefinitions
            .AsNoTracking()
            .Where(p => p.ProcessDefinitionId == processDefinitionId)
            .Select(p => p.BpmnXml)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> GetBpmnXmlByKey(string processDefinitionKey)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.ProcessDefinitions
            .AsNoTracking()
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .OrderByDescending(p => p.Version)
            .Select(p => p.BpmnXml)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.ProcessDefinitions
            .AsNoTracking()
            .Where(p => p.ProcessDefinitionKey == key && p.Version == version)
            .Select(p => p.BpmnXml)
            .FirstOrDefaultAsync();
    }

    private static ActivityInstanceSnapshot ToActivitySnapshot(
        ActivityInstanceEntry entry,
        Dictionary<Guid, ActivityInstanceState> activityStates)
    {
        if (activityStates.TryGetValue(entry.ActivityInstanceId, out var actState))
        {
            return new ActivityInstanceSnapshot(
                actState.Id, actState.ActivityId ?? entry.ActivityId, actState.ActivityType ?? "",
                actState.IsCompleted, actState.IsExecuting, actState.VariablesId,
                actState.ErrorState,
                actState.CreatedAt, actState.ExecutionStartedAt, actState.CompletedAt);
        }

        // Fallback if activity instance state not found in DB
        return new ActivityInstanceSnapshot(
            entry.ActivityInstanceId, entry.ActivityId, "",
            entry.IsCompleted, false, Guid.Empty, null,
            null, null, null);
    }

    private static ConditionSequenceSnapshot ToConditionSnapshot(
        ConditionSequenceState cs,
        WorkflowDefinition? workflowDef)
    {
        var condition = "";
        var sourceId = "";
        var targetId = "";

        if (workflowDef is not null)
        {
            var flow = workflowDef.SequenceFlows
                .OfType<ConditionalSequenceFlow>()
                .FirstOrDefault(sf => sf.SequenceFlowId == cs.ConditionalSequenceFlowId);

            if (flow is not null)
            {
                condition = flow.Condition;
                sourceId = flow.Source.ActivityId;
                targetId = flow.Target.ActivityId;
            }
        }

        return new ConditionSequenceSnapshot(
            cs.ConditionalSequenceFlowId, condition, sourceId, targetId, cs.Result);
    }
}
