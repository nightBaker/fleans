using System.Net.Http.Json;
using System.Text.Json;
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
