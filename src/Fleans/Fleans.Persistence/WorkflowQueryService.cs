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

        return definitions.Select(ProjectToSummary).ToList();
    }

    public async Task<PagedResult<ProcessDefinitionSummary>> GetAllProcessDefinitions(PageRequest page)
    {
        page = page.Normalize();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var sieveModel = new SieveModel
        {
            Sorts = page.Sorts,
            Filters = page.Filters,
            Page = page.Page,
            PageSize = page.PageSize
        };

        var baseQuery = db.ProcessDefinitions.AsQueryable();
        var filteredQuery = _sieveProcessor.Apply(sieveModel, baseQuery, applyPagination: false);
        var totalCount = await filteredQuery.CountAsync();

        var definitions = await filteredQuery
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .ToListAsync();

        var items = definitions.Select(ProjectToSummary).ToList();

        return new PagedResult<ProcessDefinitionSummary>(
            items, totalCount, page.Page, page.PageSize);
    }

    public async Task<PagedResult<ProcessDefinitionGroup>> GetProcessDefinitionGroups(PageRequest page)
    {
        page = page.Normalize();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Step 1: Apply Sieve filtering only (no sort, no pagination)
        var sieveModel = new SieveModel
        {
            Filters = page.Filters
        };
        var filteredQuery = _sieveProcessor.Apply(sieveModel, db.ProcessDefinitions.AsQueryable(),
            applyPagination: false, applySorting: false);

        // Step 2: Get distinct keys with explicit group-level ordering
        var groupedKeys = filteredQuery
            .GroupBy(d => d.ProcessDefinitionKey);

        // Step 3: Apply sort at group level
        var sortField = page.Sorts?.TrimStart('-');
        var descending = page.Sorts?.StartsWith('-') == true;

        IQueryable<string> orderedKeys = sortField switch
        {
            "DeployedAt" => descending
                ? groupedKeys.OrderByDescending(g => g.Max(d => d.DeployedAt)).Select(g => g.Key)
                : groupedKeys.OrderBy(g => g.Max(d => d.DeployedAt)).Select(g => g.Key),
            _ => descending
                ? groupedKeys.OrderByDescending(g => g.Key).Select(g => g.Key)
                : groupedKeys.OrderBy(g => g.Key).Select(g => g.Key)
        };

        var totalCount = await orderedKeys.CountAsync();

        // Step 4: Paginate keys
        var pagedKeys = await orderedKeys
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .ToListAsync();

        // Step 5: Fetch all versions for the paged keys
        var definitions = await db.ProcessDefinitions
            .Where(d => pagedKeys.Contains(d.ProcessDefinitionKey))
            .OrderBy(d => d.ProcessDefinitionKey)
            .ThenByDescending(d => d.Version)
            .ToListAsync();

        // Step 6: Build groups (preserve page order)
        var defsByKey = definitions.GroupBy(d => d.ProcessDefinitionKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        var groups = pagedKeys
            .Where(key => defsByKey.ContainsKey(key))
            .Select(key => new ProcessDefinitionGroup(
                key,
                defsByKey[key].Select(ProjectToSummary).ToList()))
            .ToList();

        return new PagedResult<ProcessDefinitionGroup>(
            groups, totalCount, page.Page, page.PageSize);
    }

    private static ProcessDefinitionSummary ProjectToSummary(ProcessDefinition d) =>
        new(d.ProcessDefinitionId, d.ProcessDefinitionKey, d.Version,
            d.DeployedAt, d.Workflow.Activities.Count,
            d.Workflow.SequenceFlows.Count, d.IsActive);

    public async Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKey(
        string processDefinitionKey, PageRequest page)
    {
        page = page.Normalize();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var definitionIds = db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == processDefinitionKey)
            .Select(p => p.ProcessDefinitionId);

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

        var definitionIds = db.ProcessDefinitions
            .Where(p => p.ProcessDefinitionKey == key && p.Version == version)
            .Select(p => p.ProcessDefinitionId);

        var baseQuery = db.WorkflowInstances
            .Where(w => definitionIds.Contains(w.ProcessDefinitionId));

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

        return ApplyUserTaskFilters(tasks, assignee, candidateGroup)
            .Select(ToUserTaskDto).ToList();
    }

    public async Task<PagedResult<UserTaskResponse>> GetPendingUserTasks(
        string? assignee, string? candidateGroup, PageRequest page)
    {
        page = page.Normalize();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Layer 1: DB-level — Sieve for TaskState filter + CreatedAt sort
        var baseQuery = db.UserTasks.AsQueryable();
        var sieveModel = new SieveModel
        {
            Sorts = page.Sorts ?? "-CreatedAt",
            Filters = page.Filters
        };
        var sievedQuery = _sieveProcessor.Apply(sieveModel, baseQuery, applyPagination: false);

        // Materialize — assignee/candidateGroup filtering must happen in memory
        // because CandidateUsers/CandidateGroups are JSON arrays not queryable in SQLite
        var tasks = await sievedQuery.ToListAsync();

        // Layer 2: Memory-level — assignee/candidateGroup filtering (preserves existing OR semantics)
        var filteredList = ApplyUserTaskFilters(tasks, assignee, candidateGroup).ToList();
        var totalCount = filteredList.Count;

        // Layer 3: Memory-level pagination
        var items = filteredList
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .Select(ToUserTaskDto)
            .ToList();

        return new PagedResult<UserTaskResponse>(
            items, totalCount, page.Page, page.PageSize);
    }

    private static IEnumerable<UserTaskState> ApplyUserTaskFilters(
        IEnumerable<UserTaskState> tasks, string? assignee, string? candidateGroup)
    {
        if (assignee is not null)
        {
            tasks = tasks.Where(t =>
                t.Assignee == assignee ||
                t.CandidateUsers.Contains(assignee));
        }

        if (candidateGroup is not null)
        {
            tasks = tasks.Where(t => t.CandidateGroups.Contains(candidateGroup));
        }

        return tasks;
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
