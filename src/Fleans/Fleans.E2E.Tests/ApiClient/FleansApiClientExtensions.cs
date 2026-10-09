using System.Net.Http.Json;
using System.Text.Json;
using Fleans.Application.QueryModels;
using Fleans.E2E.Tests.Infrastructure;

namespace Fleans.E2E.Tests.ApiClient;

/// <summary>
/// Helpers for specs whose instances are not created by <c>POST /Execution/start</c>
/// (e.g. timer start events), so the spec has no instance id up front and must discover
/// instances through <c>GET /Definitions/{key}/instances</c>.
/// Kept out of <see cref="FleansApiClient"/> to avoid merge churn on that file.
/// </summary>
public static class FleansApiClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesForKeyAsync(
        this FleansApiClient _,
        string processDefinitionKey,
        CancellationToken ct = default)
    {
        var response = await AspireFixture.ApiHttpClient.GetAsync(
            $"/Definitions/{Uri.EscapeDataString(processDefinitionKey)}/instances?page=1&pageSize=100",
            ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<WorkflowInstanceInfo>>(JsonOptions, ct);
        return page?.Items ?? [];
    }

    /// <summary>
    /// Polls <c>GET /Definitions/{key}/instances</c> until <paramref name="predicate"/> holds for
    /// the instances that are NOT in <paramref name="ignoreInstanceIds"/> (instances left over
    /// from earlier runs against the shared dev DB). Returns those new instances.
    /// </summary>
    public static async Task<IReadOnlyList<WorkflowInstanceInfo>> WaitForInstancesForKeyAsync(
        this FleansApiClient client,
        string processDefinitionKey,
        IReadOnlySet<Guid> ignoreInstanceIds,
        Func<IReadOnlyList<WorkflowInstanceInfo>, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        IReadOnlyList<WorkflowInstanceInfo> last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var all = await client.GetInstancesForKeyAsync(processDefinitionKey, ct);
            last = all.Where(i => !ignoreInstanceIds.Contains(i.InstanceId)).ToList();
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        throw new TimeoutException(
            $"Instances of '{processDefinitionKey}' did not reach the expected state within {effectiveTimeout}. " +
            $"Last seen {last.Count} new instance(s): [" +
            string.Join(", ", last.Select(i => $"{i.InstanceId:D} started={i.IsStarted} completed={i.IsCompleted}")) + "].");
    }
}
