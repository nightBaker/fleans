using System.Net.Http.Json;
using Fleans.ServiceDefaults.DTOs;

namespace Fleans.E2E.Tests.ApiClient;

// Instance migration endpoints (#1069).
public sealed partial class FleansApiClient
{
    /// <summary><c>GET /Definitions/{key}/versions/{from}/migration-preview?to={to}</c>.</summary>
    public async Task<MigrationPreviewResponse> GetMigrationPreviewAsync(string key, int from, int to, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/Definitions/{Uri.EscapeDataString(key)}/versions/{from}/migration-preview?to={to}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MigrationPreviewResponse>(JsonOptions, ct))!;
    }

    /// <summary><c>POST /Instances/{id}/migrate</c>; the caller checks the status (409 = rejected).</summary>
    public Task<HttpResponseMessage> MigrateInstanceRawAsync(Guid instanceId, MigrateInstanceRequest request, CancellationToken ct = default)
        => _http.PostAsJsonAsync($"/Instances/{instanceId}/migrate", request, JsonOptions, ct);

    /// <summary><c>POST /Instances/migrate</c>.</summary>
    public async Task<MigrateInstancesResponse> MigrateInstancesAsync(MigrateInstancesRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/Instances/migrate", request, JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"POST /Instances/migrate failed: {(int)response.StatusCode} {body}");
        return System.Text.Json.JsonSerializer.Deserialize<MigrateInstancesResponse>(body, JsonOptions)!;
    }
}
