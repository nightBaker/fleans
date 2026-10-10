using System.Net.Http.Json;
using System.Text.Json;
using Fleans.Application.DTOs;
using Fleans.Application.QueryModels;
using Fleans.ServiceDefaults.DTOs;

namespace Fleans.E2E.Tests.ApiClient;

public sealed partial class FleansApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public FleansApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<DeployBpmnResponse> DeployAsync(string bpmnXml, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/Definitions/deploy",
            new DeployBpmnRequest(bpmnXml),
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        var deployed = await response.Content.ReadFromJsonAsync<DeployBpmnResponse>(JsonOptions, ct);
        return deployed ?? throw new InvalidOperationException("Deploy returned an empty body.");
    }

    public async Task<StartWorkflowResponse> StartAsync(
        string processDefinitionKey,
        Dictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/Execution/start",
            new StartWorkflowRequest(processDefinitionKey, variables),
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        var started = await response.Content.ReadFromJsonAsync<StartWorkflowResponse>(JsonOptions, ct);
        return started ?? throw new InvalidOperationException("Start returned an empty body.");
    }

    public async Task<HttpResponseMessage> DisableAsync(string processDefinitionKey, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/Definitions/disable",
            new ProcessDefinitionKeyRequest(processDefinitionKey),
            JsonOptions,
            ct);
    }

    public async Task<HttpResponseMessage> EnableAsync(string processDefinitionKey, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/Definitions/enable",
            new ProcessDefinitionKeyRequest(processDefinitionKey),
            JsonOptions,
            ct);
    }

    public async Task<PagedResult<ProcessDefinitionSummary>> ListDefinitionsAsync(
        string? filters = null,
        CancellationToken ct = default)
    {
        var url = "/Definitions?pageSize=100";
        if (filters is not null)
        {
            url += "&filters=" + Uri.EscapeDataString(filters);
        }
        var result = await _http.GetFromJsonAsync<PagedResult<ProcessDefinitionSummary>>(url, JsonOptions, ct);
        return result ?? throw new InvalidOperationException("ListDefinitions returned an empty body.");
    }

    public async Task<PagedResult<WorkflowInstanceInfo>> ListInstancesAsync(
        string processDefinitionKey,
        int? version = null,
        CancellationToken ct = default)
    {
        var key = Uri.EscapeDataString(processDefinitionKey);
        var url = version is null
            ? $"/Definitions/{key}/instances?pageSize=100"
            : $"/Definitions/{key}/{version.Value}/instances?pageSize=100";
        var result = await _http.GetFromJsonAsync<PagedResult<WorkflowInstanceInfo>>(url, JsonOptions, ct);
        return result ?? throw new InvalidOperationException("ListInstances returned an empty body.");
    }

    public async Task<IReadOnlyList<CustomTaskCatalogEntryDto>> GetCustomTasksAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<CustomTaskCatalogEntryDto>>("/custom-tasks", JsonOptions, ct);
        return result ?? throw new InvalidOperationException("GetCustomTasks returned an empty body.");
    }

    public async Task<HttpResponseMessage> CompleteActivityAsync(
        Guid workflowInstanceId,
        string activityId,
        Dictionary<string, object>? variables = null,
        CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/Execution/complete-activity",
            new CompleteActivityRequest(workflowInstanceId, activityId, variables),
            JsonOptions,
            ct);
    }

    public async Task<EvaluateConditionsResponse> EvaluateConditionsAsync(
        string? workflowId,
        Dictionary<string, object>? variables,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/Execution/evaluate-conditions",
            new EvaluateConditionsRequest(workflowId, variables),
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EvaluateConditionsResponse>(JsonOptions, ct);
        return result ?? throw new InvalidOperationException("EvaluateConditions returned an empty body.");
    }

    public async Task<HttpResponseMessage> SendMessageRawAsync(
        string messageName,
        string? correlationKey = null,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        System.Dynamic.ExpandoObject? expando = null;
        if (variables is not null)
        {
            expando = new System.Dynamic.ExpandoObject();
            var sink = (IDictionary<string, object?>)expando;
            foreach (var kvp in variables)
            {
                sink[kvp.Key] = kvp.Value;
            }
        }
        return await _http.PostAsJsonAsync(
            "/Execution/message",
            new SendMessageRequest(messageName, correlationKey, expando),
            JsonOptions,
            ct);
    }

    public async Task<UserTaskResponse?> GetUserTaskAsync(Guid activityInstanceId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/UserTasks/{activityInstanceId:D}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserTaskResponse>(JsonOptions, ct);
    }

    public async Task<HttpResponseMessage> ClaimUserTaskAsync(
        Guid activityInstanceId,
        string userId,
        IReadOnlyList<string>? userGroups = null,
        CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            $"/UserTasks/{activityInstanceId:D}/claim",
            new ClaimTaskRequest(userId, userGroups),
            JsonOptions,
            ct);
    }

    public async Task<HttpResponseMessage> CompleteUserTaskAsync(
        Guid activityInstanceId,
        string userId,
        Dictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            $"/UserTasks/{activityInstanceId:D}/complete",
            new CompleteTaskRequest(userId, variables),
            JsonOptions,
            ct);
    }

    // --- User-task lifecycle helpers (#765) ---

    public async Task<PagedResult<UserTaskResponse>> GetPendingUserTasksAsync(
        string? assignee = null,
        string? candidateGroup = null,
        int page = 1,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (assignee is not null) query.Add($"assignee={Uri.EscapeDataString(assignee)}");
        if (candidateGroup is not null) query.Add($"candidateGroup={Uri.EscapeDataString(candidateGroup)}");
        var response = await _http.GetAsync($"/UserTasks?{string.Join("&", query)}", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<UserTaskResponse>>(JsonOptions, ct);
        return result ?? throw new InvalidOperationException("GetPendingUserTasks returned an empty body.");
    }

    public async Task<HttpResponseMessage> UnclaimUserTaskAsync(Guid activityInstanceId, CancellationToken ct = default)
    {
        return await _http.PostAsync($"/UserTasks/{activityInstanceId:D}/unclaim", content: null, ct);
    }

    public async Task<HttpResponseMessage> FailUserTaskAsync(
        Guid activityInstanceId,
        FailTaskRequest request,
        CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            $"/UserTasks/{activityInstanceId:D}/fail",
            request,
            JsonOptions,
            ct);
    }

    /// <summary>Posts a raw JSON body to <c>/fail</c> — for validation-path tests that need
    /// to omit fields the typed <see cref="FailTaskRequest"/> would always serialize.</summary>
    public async Task<HttpResponseMessage> FailUserTaskRawAsync(
        Guid activityInstanceId,
        string jsonBody,
        CancellationToken ct = default)
    {
        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return await _http.PostAsync($"/UserTasks/{activityInstanceId:D}/fail", content, ct);
    }

    /// <summary>Cancels a user task. <paramref name="request"/> = null sends an empty JSON
    /// body (<c>Content-Type: application/json</c>, zero bytes) — the reason is optional.</summary>
    public async Task<HttpResponseMessage> CancelUserTaskAsync(
        Guid activityInstanceId,
        CancelTaskRequest? request = null,
        CancellationToken ct = default)
    {
        if (request is not null)
        {
            return await _http.PostAsJsonAsync(
                $"/UserTasks/{activityInstanceId:D}/cancel",
                request,
                JsonOptions,
                ct);
        }
        using var empty = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
        return await _http.PostAsync($"/UserTasks/{activityInstanceId:D}/cancel", empty, ct);
    }

    /// <summary>Polls <c>GET /UserTasks/{id}</c> until <paramref name="predicate"/> holds.
    /// The predicate receives <c>null</c> when the endpoint returns 404 (task no longer
    /// pending) — the user-task projection is written asynchronously, so specs poll it
    /// rather than reading once.</summary>
    public async Task<UserTaskResponse?> WaitForUserTaskAsync(
        Guid activityInstanceId,
        Func<UserTaskResponse?, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effective = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow + effective;
        UserTaskResponse? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await GetUserTaskAsync(activityInstanceId, ct);
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        throw new TimeoutException(
            $"User task {activityInstanceId} did not reach the expected state within {effective}. " +
            $"Last: {(last is null ? "404" : $"State={last.TaskState}, ClaimedBy={last.ClaimedBy}")}.");
    }

    public async Task<HttpResponseMessage> SendSignalRawAsync(string signalName, CancellationToken ct = default)
    {
        return await _http.PostAsJsonAsync(
            "/Execution/signal",
            new SendSignalRequest(signalName),
            JsonOptions,
            ct);
    }

    public async Task<InstanceStateSnapshot?> GetStateAsync(Guid workflowInstanceId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/Instances/{workflowInstanceId:D}/state", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InstanceStateSnapshot>(JsonOptions, ct);
    }

    public async Task<SendMessageResponse> SendMessageAsync(
        string messageName,
        string? correlationKey = null,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        // Controller expects ExpandoObject? for Variables; hand-build one from the dict so callers
        // don't have to construct ExpandoObjects directly.
        System.Dynamic.ExpandoObject? expando = null;
        if (variables is not null)
        {
            expando = new System.Dynamic.ExpandoObject();
            var sink = (IDictionary<string, object?>)expando;
            foreach (var kvp in variables)
            {
                sink[kvp.Key] = kvp.Value;
            }
        }

        var response = await _http.PostAsJsonAsync(
            "/Execution/message",
            new SendMessageRequest(messageName, correlationKey, expando),
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>(JsonOptions, ct);
        return result ?? throw new InvalidOperationException("SendMessage returned an empty body.");
    }

    public async Task<SendSignalResponse> SendSignalAsync(string signalName, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/Execution/signal",
            new SendSignalRequest(signalName),
            JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SendSignalResponse>(JsonOptions, ct);
        return result ?? throw new InvalidOperationException("SendSignal returned an empty body.");
    }

    public async Task<InstanceStateSnapshot> WaitForStateAsync(
        Guid workflowInstanceId,
        Func<InstanceStateSnapshot, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        InstanceStateSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await GetStateAsync(workflowInstanceId, ct);
            if (last is not null && predicate(last))
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        throw new TimeoutException(
            $"Workflow instance {workflowInstanceId} did not reach the expected state within {timeout ?? TimeSpan.FromSeconds(30)}. " +
            $"Last snapshot: IsStarted={last?.IsStarted}, IsCompleted={last?.IsCompleted}, " +
            $"Active=[{string.Join(",", last?.ActiveActivityIds ?? new List<string>())}], " +
            $"Completed=[{string.Join(",", last?.CompletedActivityIds ?? new List<string>())}].");
    }

    public async Task<InstanceStateSnapshot> WaitForCompletionAsync(
        Guid workflowInstanceId,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        InstanceStateSnapshot? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await GetStateAsync(workflowInstanceId, ct);
            if (last is { IsCompleted: true })
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        throw new TimeoutException(
            $"Workflow instance {workflowInstanceId} did not reach IsCompleted within {timeout ?? TimeSpan.FromSeconds(30)}. " +
            $"Last snapshot: IsStarted={last?.IsStarted}, IsCompleted={last?.IsCompleted}, " +
            $"Active=[{string.Join(",", last?.ActiveActivityIds ?? new List<string>())}], " +
            $"Completed=[{string.Join(",", last?.CompletedActivityIds ?? new List<string>())}].");
    }
}
