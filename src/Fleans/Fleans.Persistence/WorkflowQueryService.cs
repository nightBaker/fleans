using System.Dynamic;
using Fleans.Application;
using Fleans.Application.DTOs;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Sieve.Models;
using Sieve.Services;

namespace Fleans.Persistence;

public class WorkflowQueryService : IWorkflowQueryService
{
    private readonly IDbContextFactory<FleanQueryDbContext> _dbContextFactory;
    private readonly ISieveProcessor _sieveProcessor;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        PreserveReferencesHandling = PreserveReferencesHandling.Objects,
        SerializationBinder = new DomainAssemblySerializationBinder()
    };

    public WorkflowQueryService(
        IDbContextFactory<FleanQueryDbContext> dbContextFactory,
        ISieveProcessor sieveProcessor)
    {
        _dbContextFactory = dbContextFactory;
        _sieveProcessor = sieveProcessor;
    }

    public async Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var state = await db.WorkflowInstances
            .Include(s => s.Entries)
            .Include(s => s.VariableStates)
            .Include(s => s.ConditionSequenceStates)
            .FirstOrDefaultAsync(s => s.Id == workflowInstanceId);

        if (state is null)
            return null;

        // Load process definition for condition enrichment
        WorkflowDefinition? workflowDef = null;
        if (state.ProcessDefinitionId is not null)
        {
            var processDef = await db.ProcessDefinitions
                .FirstOrDefaultAsync(p => p.ProcessDefinitionId == state.ProcessDefinitionId);
            workflowDef = processDef?.Workflow;
        }

        var activeEntries = state.Entries.Where(e => !e.IsCompleted).ToList();
        var completedEntries = state.Entries.Where(e => e.IsCompleted).ToList();

        var activeSnapshots = activeEntries
            .Select(ToActivitySnapshot)
            .ToList();
        var completedSnapshots = completedEntries
            .Select(ToActivitySnapshot)
            .ToList();

        var activeIds = activeEntries.Select(e => e.ActivityId).ToList();
        var completedIds = completedEntries.Select(e => e.ActivityId).ToList();

        var variableStates = state.VariableStates.Select(vs =>
        {
            var dict = ((IDictionary<string, object>)vs.Variables)
                .ToDictionary(e => e.Key, e => FormatVariableValue(e.Value));
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
            .OrderBy(d => d.ProcessDefinitionKey)
            .ThenBy(d => d.Version)
            .ToListAsync();

        return definitions.Select(d => new ProcessDefinitionSummary(
            d.ProcessDefinitionId,
            d.ProcessDefinitionKey,
            d.Version,
            d.DeployedAt,
            d.Workflow.Activities.Count,
            d.Workflow.SequenceFlows.Count,
            d.IsActive)).ToList();
    }

    public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionIds = await db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .Select(p => p.ProcessDefinitionId)
            .ToListAsync();

        var instances = await db.WorkflowInstances
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
            .Where(p => p.ProcessDefinitionKey == key && p.Version == version)
            .Select(p => p.ProcessDefinitionId)
            .FirstOrDefaultAsync();

        if (definitionId is null)
            return [];

        var instances = await db.WorkflowInstances
            .Where(w => w.ProcessDefinitionId == definitionId)
            .ToListAsync();

        return instances.Select(w => new WorkflowInstanceInfo(
            w.Id, w.ProcessDefinitionId ?? "", w.IsStarted, w.IsCompleted,
            w.CreatedAt, w.ExecutionStartedAt, w.CompletedAt)).ToList();
    }

    public async Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKey(
        string processDefinitionKey, PageRequest page)
    {
        page = page.Normalize();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionIds = await db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .Select(p => p.ProcessDefinitionId)
            .ToListAsync();

        var baseQuery = db.WorkflowInstances
            .Where(w => w.ProcessDefinitionId != null
                && definitionIds.Contains(w.ProcessDefinitionId));

        return await ApplySieveAndProject(baseQuery, page);
    }

    public async Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(
        string key, int version, PageRequest page)
    {
        page = page.Normalize();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionId = await db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == key && p.Version == version)
            .Select(p => p.ProcessDefinitionId)
            .FirstOrDefaultAsync();

        if (definitionId is null)
            return new PagedResult<WorkflowInstanceInfo>([], 0, page.Page, page.PageSize);

        var baseQuery = db.WorkflowInstances
            .Where(w => w.ProcessDefinitionId == definitionId);

        return await ApplySieveAndProject(baseQuery, page);
    }

    private async Task<PagedResult<WorkflowInstanceInfo>> ApplySieveAndProject(
        IQueryable<WorkflowInstanceState> baseQuery, PageRequest page)
    {
        var sieveModel = new SieveModel
        {
            Sorts = page.Sorts,
            Filters = page.Filters,
            Page = page.Page,
            PageSize = page.PageSize
        };

        // Apply filters and sorting without pagination to get total count
        var filteredQuery = _sieveProcessor.Apply(sieveModel, baseQuery, applyPagination: false);
        var totalCount = await filteredQuery.CountAsync();

        // Apply pagination manually on the already-filtered-and-sorted query
        var pagedQuery = filteredQuery
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize);

        var items = await pagedQuery.Select(w => new WorkflowInstanceInfo(
            w.Id, w.ProcessDefinitionId ?? "", w.IsStarted, w.IsCompleted,
            w.CreatedAt, w.ExecutionStartedAt, w.CompletedAt)).ToListAsync();

        return new PagedResult<WorkflowInstanceInfo>(
            items, totalCount, page.Page, page.PageSize);
    }

    public async Task<string?> GetBpmnXml(Guid instanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var processDefinitionId = await db.WorkflowInstances
            .Where(w => w.Id == instanceId)
            .Select(w => w.ProcessDefinitionId)
            .FirstOrDefaultAsync();

        if (processDefinitionId is null)
            return null;

        return await db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionId == processDefinitionId)
            .Select(p => p.BpmnXml)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> GetBpmnXmlByKey(string processDefinitionKey)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .OrderByDescending(p => p.Version)
            .Select(p => p.BpmnXml)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == key && p.Version == version)
            .Select(p => p.BpmnXml)
            .FirstOrDefaultAsync();
    }

    private static ActivityInstanceSnapshot ToActivitySnapshot(ActivityInstanceEntry entry)
    {
        return new ActivityInstanceSnapshot(
            entry.ActivityInstanceId, entry.ActivityId, entry.ActivityType ?? "",
            entry.IsCompleted, entry.IsExecuting, entry.IsCancelled, entry.VariablesId,
            entry.ErrorState, entry.CancellationReason,
            entry.CreatedAt, entry.ExecutionStartedAt, entry.CompletedAt,
            entry.ChildWorkflowInstanceId);
    }

    private static string FormatVariableValue(object? value) => value switch
    {
        null => "",
        string s => s,
        IList<object> or IDictionary<string, object> => JsonConvert.SerializeObject(value, Formatting.None),
        _ => value.ToString() ?? ""
    };

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

    public async Task<IReadOnlyList<UserTaskResponse>> GetPendingUserTasks(
        string? assignee = null, string? candidateGroup = null)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var tasks = await db.UserTasks
            .Where(t => t.TaskState != UserTaskLifecycleState.Completed)
            .ToListAsync();

        IEnumerable<UserTaskState> filtered = tasks;

        if (assignee is not null)
        {
            filtered = filtered.Where(t =>
                t.Assignee == assignee ||
                t.CandidateUsers.Contains(assignee));
        }

        if (candidateGroup is not null)
        {
            filtered = filtered.Where(t => t.CandidateGroups.Contains(candidateGroup));
        }

        return filtered.Select(ToUserTaskDto).ToList();
    }

    public async Task<UserTaskResponse?> GetUserTask(Guid activityInstanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var task = await db.UserTasks
            .FirstOrDefaultAsync(t => t.ActivityInstanceId == activityInstanceId);

        return task is null ? null : ToUserTaskDto(task);
    }

    public async Task<IReadOnlyList<UserTaskState>> GetActiveUserTasksForWorkflow(Guid workflowInstanceId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.UserTasks
            .Where(t => t.WorkflowInstanceId == workflowInstanceId
                        && t.TaskState != UserTaskLifecycleState.Completed)
            .ToListAsync();
    }

    private static UserTaskResponse ToUserTaskDto(UserTaskState t) =>
        new(t.WorkflowInstanceId, t.ActivityInstanceId, t.ActivityId,
            t.Assignee, t.CandidateGroups, t.CandidateUsers,
            t.ClaimedBy, t.TaskState.ToString(), t.CreatedAt,
            t.ExpectedOutputVariables);
}
