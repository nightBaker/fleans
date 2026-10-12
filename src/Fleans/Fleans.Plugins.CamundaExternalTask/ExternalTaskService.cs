using System.Dynamic;
using System.Text.Json;
using Fleans.Application.CustomTasks;
using Fleans.Application.Grains;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>A Camunda-shaped REST error: HTTP status + <c>{type, message}</c> body.</summary>
public sealed class ExternalTaskApiException(int statusCode, string type, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Type { get; } = type;
}

/// <summary>Filters of <c>GET /external-task</c> and <c>GET /external-task/count</c>.</summary>
public sealed record ExternalTaskQuery(
    string? ExternalTaskId = null,
    string? TopicName = null,
    string? WorkerId = null,
    bool Locked = false,
    bool NotLocked = false,
    bool WithRetriesLeft = false,
    bool NoRetriesLeft = false,
    string? ProcessInstanceId = null,
    string? ActivityId = null,
    string? ProcessDefinitionId = null,
    string? ProcessDefinitionKey = null,
    int? FirstResult = null,
    int? MaxResults = null);

/// <summary>
/// Camunda 7 external-task semantics on top of <see cref="ExternalTaskDbContext"/> and the engine's
/// narrow <see cref="IWorkflowInstanceCallback"/>. Locks are claimed with a conditional
/// <c>UPDATE … WHERE lock is free</c>, so any number of API replicas can share one table.
/// </summary>
public sealed partial class ExternalTaskService(
    IDbContextFactory<ExternalTaskDbContext> dbFactory,
    IGrainFactory grainFactory,
    TimeProvider timeProvider,
    ILogger<ExternalTaskService> logger)
{
    /// <summary>Camunda's upper bound for <c>asyncResponseTimeout</c>.</summary>
    private static readonly TimeSpan MaxAsyncResponseTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LongPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a task stays locked while a worker's report is written into the workflow.</summary>
    private static readonly TimeSpan ReportLockDuration = TimeSpan.FromMinutes(5);

    /// <summary>Error code a Fleans error boundary sees when a worker exhausts its retries.</summary>
    public const string RetriesExhaustedErrorCode = "500";

    // ── fetch & lock ─────────────────────────────────────────────────────────

    public async Task<List<LockedExternalTaskDto>> FetchAndLock(FetchExternalTasksDto request, CancellationToken ct)
    {
        var workerId = RequireWorkerId(request.WorkerId);
        if (request.MaxTasks < 0)
            throw BadRequest("maxTasks must not be negative.");
        var topics = request.Topics ?? [];
        foreach (var topic in topics)
        {
            if (string.IsNullOrEmpty(topic.TopicName))
                throw BadRequest("topicName is required.");
            if (topic.LockDuration <= 0)
                throw BadRequest($"lockDuration of topic '{topic.TopicName}' must be greater than 0.");
            AddMilliseconds(timeProvider.GetUtcNow().UtcDateTime, topic.LockDuration, "lockDuration"); // validates the range
        }

        var timeout = request.AsyncResponseTimeout is > 0
            ? TimeSpan.FromMilliseconds(Math.Min(request.AsyncResponseTimeout.Value, MaxAsyncResponseTimeout.TotalMilliseconds))
            : TimeSpan.Zero;
        var deadline = timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            var locked = await TryFetchAndLock(workerId, request.MaxTasks, request.UsePriority, topics, ct);
            var remaining = deadline - timeProvider.GetUtcNow();
            if (locked.Count > 0 || remaining <= TimeSpan.Zero)
                return locked;
            await Task.Delay(remaining < LongPollInterval ? remaining : LongPollInterval, timeProvider, ct);
        }
    }

    private async Task<List<LockedExternalTaskDto>> TryFetchAndLock(
        string workerId, int maxTasks, bool usePriority, List<FetchExternalTaskTopicDto> topics, CancellationToken ct)
    {
        var result = new List<LockedExternalTaskDto>();
        if (maxTasks == 0 || topics.Count == 0) return result;

        var topicNames = topics.Select(t => t.TopicName).Distinct().ToList();
        var tried = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        while (result.Count < maxTasks)
        {
            var now = UtcNow();
            var query = db.ExternalTasks.AsNoTracking().Where(t =>
                topicNames.Contains(t.TopicName)
                && (t.Retries == null || t.Retries > 0)
                && (t.LockExpirationTime == null || t.LockExpirationTime <= now)
                && !tried.Contains(t.Id));
            query = usePriority
                ? query.OrderByDescending(t => t.Priority).ThenBy(t => t.CreateTime)
                : query.OrderBy(t => t.CreateTime);
            var candidates = await query.Take(Math.Max(maxTasks - result.Count, 10)).ToListAsync(ct);
            if (candidates.Count == 0) break;

            foreach (var candidate in candidates)
            {
                if (result.Count >= maxTasks) break;
                tried.Add(candidate.Id);

                var topic = topics.FirstOrDefault(t => t.TopicName == candidate.TopicName && Matches(t, candidate));
                if (topic is null) continue;

                var claimNow = UtcNow(); // fresh per claim: grain calls above may have taken a while
                var expires = AddMilliseconds(claimNow, topic.LockDuration, "lockDuration");
                var claimed = await db.ExternalTasks
                    .Where(t => t.Id == candidate.Id && (t.LockExpirationTime == null || t.LockExpirationTime <= claimNow))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.WorkerId, workerId)
                        .SetProperty(t => t.LockExpirationTime, expires), ct);
                if (claimed == 0) continue; // another worker won the race

                var callback = grainFactory.GetGrain<IWorkflowInstanceCallback>(candidate.WorkflowInstanceId);
                ExpandoObject? variables;
                try
                {
                    variables = await callback.GetVariablesIfActive(candidate.ActivityInstanceId, candidate.VariablesId);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    // Engine unreachable for this task: hand it back and keep serving the others.
                    LogVariablesReadFailed(ex, candidate.Id, candidate.TopicName);
                    await SetLock(candidate.Id, null, null);
                    continue;
                }
                catch (Exception ex)
                {
                    // Deterministic (e.g. unknown variable scope): no retry can fix it — fail visibly.
                    LogVariablesReadFailed(ex, candidate.Id, candidate.TopicName);
                    await callback.FailActivity(candidate.ActivityId, candidate.ActivityInstanceId, ex);
                    await Delete(candidate.Id);
                    continue;
                }
                if (variables is null)
                {
                    // The activity ended without a worker (cancelled, interrupted by a boundary event, …).
                    await db.ExternalTasks.Where(t => t.Id == candidate.Id).ExecuteDeleteAsync(ct);
                    LogStaleTaskRemoved(candidate.Id, candidate.TopicName);
                    continue;
                }

                candidate.WorkerId = workerId;
                candidate.LockExpirationTime = expires;
                result.Add(ToLockedDto(candidate, variables, topic));
                LogLocked(candidate.Id, candidate.TopicName, workerId);
            }
        }

        return result;
    }

    private static bool Matches(FetchExternalTaskTopicDto topic, ExternalTaskEntity task) =>
        (topic.ProcessDefinitionKey is null || topic.ProcessDefinitionKey == task.ProcessDefinitionKey)
        && (topic.ProcessDefinitionKeyIn is not { Count: > 0 } || topic.ProcessDefinitionKeyIn.Contains(task.ProcessDefinitionKey))
        && (topic.ProcessDefinitionId is null || topic.ProcessDefinitionId == task.ProcessDefinitionId)
        && (topic.ProcessDefinitionIdIn is not { Count: > 0 }
            || (task.ProcessDefinitionId is not null && topic.ProcessDefinitionIdIn.Contains(task.ProcessDefinitionId)));

    private static LockedExternalTaskDto ToLockedDto(
        ExternalTaskEntity task, ExpandoObject processVariables, FetchExternalTaskTopicDto topic)
    {
        var all = (IDictionary<string, object?>)processVariables;
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var mapping in Deserialize<InputMapping>(task.InputMappingsJson))
            locals[mapping.Target] = MappingResolver.Resolve(mapping.Source, all);

        var visible = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!topic.LocalVariables)
            foreach (var (name, value) in all) visible[name] = value;
        foreach (var (name, value) in locals) visible[name] = value;

        var selected = topic.Variables is null
            ? visible
            : visible.Where(kv => topic.Variables.Contains(kv.Key));

        return new LockedExternalTaskDto(
            Id: task.Id,
            TopicName: task.TopicName,
            WorkerId: task.WorkerId!,
            LockExpirationTime: CamundaVariables.FormatDate(task.LockExpirationTime!.Value),
            CreateTime: CamundaVariables.FormatDate(task.CreateTime),
            ActivityId: task.ActivityId,
            ActivityInstanceId: task.ActivityInstanceId.ToString("D"),
            ExecutionId: task.ActivityInstanceId.ToString("D"),
            ProcessInstanceId: task.WorkflowInstanceId.ToString("D"),
            ProcessDefinitionId: task.ProcessDefinitionId,
            ProcessDefinitionKey: task.ProcessDefinitionKey,
            ProcessDefinitionVersionTag: null,
            Retries: task.Retries,
            ErrorMessage: task.ErrorMessage,
            ErrorDetails: task.ErrorDetails,
            TenantId: null,
            Priority: task.Priority,
            BusinessKey: null,
            Variables: CamundaVariables.ToCamundaVariables(selected),
            ExtensionProperties: new Dictionary<string, string>());
    }

    // ── report back ──────────────────────────────────────────────────────────

    public async Task Complete(string id, CompleteExternalTaskDto request)
    {
        var variables = ToFleans(request.Variables);
        var localVariables = ToFleans(request.LocalVariables);
        var task = await ClaimForReport(id, request.WorkerId, "completed");

        // `variables` land in the workflow scope; output mappings may also read the local ones.
        var outputs = new ExpandoObject();
        var outputsDict = (IDictionary<string, object?>)outputs;
        foreach (var (name, value) in variables) outputsDict[name] = value;
        var outputMappings = Deserialize<OutputMapping>(task.OutputMappingsJson);
        if (outputMappings.Count > 0)
        {
            var scope = new Dictionary<string, object?>(variables, StringComparer.Ordinal);
            foreach (var (name, value) in localVariables) scope[name] = value;
            foreach (var mapping in outputMappings)
                outputsDict[mapping.Target] = MappingResolver.Resolve(mapping.Source, scope);
        }

        await Callback(task).CompleteActivity(task.ActivityId, task.ActivityInstanceId, outputs);
        await Delete(id);
        LogCompleted(id, task.TopicName, task.WorkerId);
    }

    public async Task HandleFailure(string id, ExternalTaskFailureDto request)
    {
        if (request.Retries is not { } retries || retries < 0)
            throw BadRequest("retries must be a number greater than or equal to 0.");
        if (request.RetryTimeout < 0)
            throw BadRequest("retryTimeout must not be negative.");
        var worker = RequireWorkerId(request.WorkerId);

        if (retries == 0)
        {
            var task = await ClaimForReport(id, worker, "failed");
            await FailActivity(task, request.ErrorMessage, request.ErrorDetails);
            return;
        }

        var retryAt = AddMilliseconds(UtcNow(), request.RetryTimeout, "retryTimeout");
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks.Where(t => t.Id == id && t.WorkerId == worker).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Retries, retries)
            .SetProperty(t => t.ErrorMessage, request.ErrorMessage)
            .SetProperty(t => t.ErrorDetails, request.ErrorDetails)
            .SetProperty(t => t.WorkerId, (string?)null)
            .SetProperty(t => t.LockExpirationTime, retryAt));
        if (updated == 0)
            throw await WrongWorker(id, worker, "failed");
        LogFailureRetry(id, retries, request.RetryTimeout);
    }

    public async Task HandleBpmnError(string id, ExternalTaskBpmnErrorDto request)
    {
        if (string.IsNullOrEmpty(request.ErrorCode))
            throw BadRequest("errorCode is required.");
        var task = await ClaimForReport(id, request.WorkerId, "reported a BPMN error for");
        if (request.Variables is { Count: > 0 })
            LogBpmnErrorVariablesIgnored(id, request.Variables.Count);

        await Callback(task).FailActivity(task.ActivityId, task.ActivityInstanceId,
            new CustomTaskFailedActivityException(request.ErrorCode, request.ErrorMessage ?? request.ErrorCode));
        await Delete(id);
        LogBpmnError(id, task.TopicName, request.ErrorCode);
    }

    // ── lock management ──────────────────────────────────────────────────────

    public async Task ExtendLock(string id, ExtendLockDto request)
    {
        var workerId = RequireWorkerId(request.WorkerId);
        if (request.NewDuration <= 0)
            throw BadRequest("newDuration must be greater than 0.");
        await Load(id);
        var now = UtcNow();
        var expires = AddMilliseconds(now, request.NewDuration, "newDuration");
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id && t.WorkerId == workerId && t.LockExpirationTime > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LockExpirationTime, expires));
        if (updated == 0)
            throw BadRequest($"The lock of the External Task {id} cannot be extended by worker '{workerId}'. " +
                             "It is not locked by this worker or the lock has expired.");
    }

    public async Task Lock(string id, LockExternalTaskDto request)
    {
        var workerId = RequireWorkerId(request.WorkerId);
        if (request.LockDuration <= 0)
            throw BadRequest("lockDuration must be greater than 0.");
        await Load(id);
        var now = UtcNow();
        var expires = AddMilliseconds(now, request.LockDuration, "lockDuration");
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ExternalTasks
            .Where(t => t.Id == id
                && (t.WorkerId == null || t.WorkerId == workerId || t.LockExpirationTime == null || t.LockExpirationTime <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.WorkerId, workerId)
                .SetProperty(t => t.LockExpirationTime, expires));
        if (updated == 0)
            throw BadRequest($"External Task {id} is already locked by another worker.");
    }

    public async Task Unlock(string id)
    {
        await Load(id);
        await SetLock(id, null, null);
    }

    public async Task SetRetries(string id, RetriesDto request)
    {
        if (request.Retries is not { } retries || retries < 0)
            throw BadRequest("retries must be a number greater than or equal to 0.");
        var task = await Load(id);
        if (retries == 0)
        {
            await FailActivity(task, task.ErrorMessage, task.ErrorDetails);
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExternalTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Retries, retries));
    }

    public async Task SetPriority(string id, PriorityDto request)
    {
        await Load(id);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExternalTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Priority, request.Priority));
    }

    // ── queries ──────────────────────────────────────────────────────────────

    public async Task<ExternalTaskDto> Get(string id) => ToDto(await Load(id));

    public async Task<List<ExternalTaskDto>> Query(ExternalTaskQuery query)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        IQueryable<ExternalTaskEntity> q = Filter(db.ExternalTasks.AsNoTracking(), query).OrderBy(t => t.CreateTime);
        if (query.FirstResult is > 0) q = q.Skip(query.FirstResult.Value);
        if (query.MaxResults is >= 0) q = q.Take(query.MaxResults.Value);
        return (await q.ToListAsync()).Select(ToDto).ToList();
    }

    public async Task<long> Count(ExternalTaskQuery query)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Filter(db.ExternalTasks.AsNoTracking(), query).LongCountAsync();
    }

    public async Task<List<string>> TopicNames(bool withLockedTasks, bool withUnlockedTasks, bool withRetriesLeft)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = Filter(db.ExternalTasks.AsNoTracking(), new ExternalTaskQuery(
            Locked: withLockedTasks, NotLocked: withUnlockedTasks, WithRetriesLeft: withRetriesLeft));
        return await q.Select(t => t.TopicName).Distinct().OrderBy(t => t).ToListAsync();
    }

    private IQueryable<ExternalTaskEntity> Filter(IQueryable<ExternalTaskEntity> q, ExternalTaskQuery f)
    {
        var now = UtcNow();
        if (f.ExternalTaskId is not null) q = q.Where(t => t.Id == f.ExternalTaskId);
        if (f.TopicName is not null) q = q.Where(t => t.TopicName == f.TopicName);
        if (f.WorkerId is not null) q = q.Where(t => t.WorkerId == f.WorkerId);
        if (f.Locked) q = q.Where(t => t.WorkerId != null && t.LockExpirationTime > now);
        if (f.NotLocked) q = q.Where(t => t.WorkerId == null || t.LockExpirationTime == null || t.LockExpirationTime <= now);
        if (f.WithRetriesLeft) q = q.Where(t => t.Retries == null || t.Retries > 0);
        if (f.NoRetriesLeft) q = q.Where(t => t.Retries == 0);
        if (f.ProcessInstanceId is not null && Guid.TryParse(f.ProcessInstanceId, out var instanceId))
            q = q.Where(t => t.WorkflowInstanceId == instanceId);
        else if (f.ProcessInstanceId is not null)
            q = q.Where(_ => false);
        if (f.ActivityId is not null) q = q.Where(t => t.ActivityId == f.ActivityId);
        if (f.ProcessDefinitionId is not null) q = q.Where(t => t.ProcessDefinitionId == f.ProcessDefinitionId);
        if (f.ProcessDefinitionKey is not null) q = q.Where(t => t.ProcessDefinitionKey == f.ProcessDefinitionKey);
        return q;
    }

    private static ExternalTaskDto ToDto(ExternalTaskEntity t) => new(
        Id: t.Id,
        TopicName: t.TopicName,
        WorkerId: t.WorkerId,
        LockExpirationTime: t.LockExpirationTime is { } l ? CamundaVariables.FormatDate(l) : null,
        CreateTime: CamundaVariables.FormatDate(t.CreateTime),
        ActivityId: t.ActivityId,
        ActivityInstanceId: t.ActivityInstanceId.ToString("D"),
        ExecutionId: t.ActivityInstanceId.ToString("D"),
        ProcessInstanceId: t.WorkflowInstanceId.ToString("D"),
        ProcessDefinitionId: t.ProcessDefinitionId,
        ProcessDefinitionKey: t.ProcessDefinitionKey,
        ProcessDefinitionVersionTag: null,
        Retries: t.Retries,
        ErrorMessage: t.ErrorMessage,
        Suspended: false,
        TenantId: null,
        Priority: t.Priority,
        BusinessKey: null);

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task FailActivity(ExternalTaskEntity task, string? errorMessage, string? errorDetails)
    {
        var message = errorMessage ?? "External task failed with no retries left";
        if (!string.IsNullOrEmpty(errorDetails))
            message += Environment.NewLine + errorDetails;
        await Callback(task).FailActivity(task.ActivityId, task.ActivityInstanceId,
            new CustomTaskFailedActivityException(RetriesExhaustedErrorCode, message));
        await Delete(task.Id);
        LogFailedNoRetries(task.Id, task.TopicName);
    }

    /// <summary>
    /// Atomically confirms <paramref name="workerId"/> still owns the task and pushes its lock out
    /// for the duration of the report, so no concurrent <c>fetchAndLock</c> can hand it to another
    /// worker while the result is being written into the workflow. Two reports for one task cannot
    /// both pass this check unless they come from the same worker.
    /// </summary>
    private async Task<ExternalTaskEntity> ClaimForReport(string id, string? workerId, string action)
    {
        var worker = RequireWorkerId(workerId);
        await Load(id);
        var reportLock = UtcNow() + ReportLockDuration;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var updated = await db.ExternalTasks.Where(t => t.Id == id && t.WorkerId == worker)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LockExpirationTime, reportLock));
            if (updated == 0)
                throw await WrongWorker(id, worker, action);
        }
        return await Load(id);
    }

    private async Task<ExternalTaskApiException> WrongWorker(string id, string worker, string action)
    {
        var task = await Load(id);
        return new ExternalTaskApiException(500, "BadUserRequestException",
            $"External Task {id} cannot be {action} by worker '{worker}'. " +
            $"It is locked by worker '{task.WorkerId}'.");
    }

    private async Task<ExternalTaskEntity> Load(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ExternalTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new ExternalTaskApiException(404, "InvalidRequestException", $"External task with id {id} does not exist");
    }

    private async Task SetLock(string id, string? workerId, DateTime? expires)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExternalTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.WorkerId, workerId)
            .SetProperty(t => t.LockExpirationTime, expires));
    }

    private async Task Delete(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExternalTasks.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    private IWorkflowInstanceCallback Callback(ExternalTaskEntity task) =>
        grainFactory.GetGrain<IWorkflowInstanceCallback>(task.WorkflowInstanceId);

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime AddMilliseconds(DateTime now, long milliseconds, string name) =>
        milliseconds <= (DateTime.MaxValue - now).TotalMilliseconds
            ? now.AddMilliseconds(milliseconds)
            : throw BadRequest($"{name} is too large.");

    /// <summary>Same split as <c>CustomTaskHandlerBase</c>: infrastructure failures a retry can fix.</summary>
    private static bool IsTransient(Exception ex) => ex is TimeoutException or OrleansException;

    private static Dictionary<string, object?> ToFleans(Dictionary<string, VariableValueDto>? variables)
    {
        try { return CamundaVariables.ToFleans(variables); }
        catch (Exception ex) when (ex is CamundaVariables.UnsupportedVariableException or FormatException
                                       or InvalidOperationException or JsonException)
        {
            throw BadRequest(ex.Message);
        }
    }

    private static List<T> Deserialize<T>(string json) => JsonSerializer.Deserialize<List<T>>(json) ?? [];

    private static string RequireWorkerId(string? workerId) =>
        string.IsNullOrEmpty(workerId) ? throw BadRequest("workerId is required.") : workerId;

    private static ExternalTaskApiException BadRequest(string message) =>
        new(400, "InvalidRequestException", message);

    [LoggerMessage(EventId = 9360, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') locked by worker '{WorkerId}'")]
    private partial void LogLocked(string externalTaskId, string topicName, string workerId);

    [LoggerMessage(EventId = 9361, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') completed by worker '{WorkerId}'")]
    private partial void LogCompleted(string externalTaskId, string topicName, string? workerId);

    [LoggerMessage(EventId = 9362, Level = LogLevel.Warning,
        Message = "External task {ExternalTaskId} failed; {Retries} retries left, next attempt in {RetryTimeoutMs} ms")]
    private partial void LogFailureRetry(string externalTaskId, int retries, long retryTimeoutMs);

    [LoggerMessage(EventId = 9363, Level = LogLevel.Error,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') has no retries left — failing the activity")]
    private partial void LogFailedNoRetries(string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9364, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') reported BPMN error '{ErrorCode}'")]
    private partial void LogBpmnError(string externalTaskId, string topicName, string errorCode);

    [LoggerMessage(EventId = 9365, Level = LogLevel.Information,
        Message = "External task {ExternalTaskId} (topic '{TopicName}') removed — its activity is no longer active")]
    private partial void LogStaleTaskRemoved(string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9367, Level = LogLevel.Error,
        Message = "Reading variables for external task {ExternalTaskId} (topic '{TopicName}') failed")]
    private partial void LogVariablesReadFailed(Exception ex, string externalTaskId, string topicName);

    [LoggerMessage(EventId = 9366, Level = LogLevel.Warning,
        Message = "BPMN error for external task {ExternalTaskId} carried {Count} variable(s); Fleans ignores variables on BPMN errors")]
    private partial void LogBpmnErrorVariablesIgnored(string externalTaskId, int count);
}
