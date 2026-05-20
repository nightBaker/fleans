using System.Net.Http.Json;
using System.Text.Json;
using Fleans.Application.DTOs;
using Fleans.Application.QueryModels;
using Fleans.ServiceDefaults.DTOs;

namespace Fleans.E2E.Tests.ApiClient;

public sealed class FleansApiClient
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
