using System.Net.Http.Json;
using Fleans.Application.QueryModels;

namespace Fleans.E2E.Tests.ApiClient;

// Helpers for process-definition versioning specs (#767). Kept in a separate partial file so
// concurrent additions to FleansApiClient.cs don't conflict.
public sealed partial class FleansApiClient
{
    /// <summary><c>GET /Definitions/{key}/instances</c> — instances across every version of a key.</summary>
    public async Task<PagedResult<WorkflowInstanceInfo>> GetInstancesPageByKeyAsync(
        string processDefinitionKey,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        var url = $"/Definitions/{Uri.EscapeDataString(processDefinitionKey)}/instances?page=1&pageSize={pageSize}";
        return await GetPageAsync(url, ct);
    }

    /// <summary><c>GET /Definitions/{key}/{version}/instances</c> — instances pinned to one version.</summary>
    public async Task<PagedResult<WorkflowInstanceInfo>> GetInstancesPageByKeyAndVersionAsync(
        string processDefinitionKey,
        int version,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        var url = $"/Definitions/{Uri.EscapeDataString(processDefinitionKey)}/{version}/instances?page=1&pageSize={pageSize}";
        return await GetPageAsync(url, ct);
    }

    /// <summary>
    /// Polls an instance listing until <paramref name="predicate"/> holds. The listings read the
    /// eventually-consistent EF projection, so a freshly-started instance may not appear immediately.
    /// </summary>
    public async Task<PagedResult<WorkflowInstanceInfo>> WaitForInstancesPageAsync(
        Func<CancellationToken, Task<PagedResult<WorkflowInstanceInfo>>> fetch,
        Func<PagedResult<WorkflowInstanceInfo>, bool> predicate,
        string description,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        PagedResult<WorkflowInstanceInfo>? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await fetch(ct);
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        throw new TimeoutException(
            $"Instance listing did not satisfy '{description}' within {effectiveTimeout}. " +
            $"Last page: TotalCount={last?.TotalCount}, Items=[" +
            string.Join(", ", last?.Items.Select(i => $"{i.InstanceId}@{i.ProcessDefinitionId} completed={i.IsCompleted}")
                ?? Enumerable.Empty<string>()) + "].");
    }

    private async Task<PagedResult<WorkflowInstanceInfo>> GetPageAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<WorkflowInstanceInfo>>(JsonOptions, ct);
        return page ?? throw new InvalidOperationException($"GET {url} returned an empty body.");
    }
}
