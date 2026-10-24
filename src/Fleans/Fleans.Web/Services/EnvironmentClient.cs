using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.Extensions.Options;

namespace Fleans.Web.Services;

/// <summary>
/// Talks to the other environments of <see cref="EnvironmentOptions.PromotionTargets"/> (#900):
/// promotes a version by submitting its BPMN XML to the target's <c>POST /Deployments/requests</c>,
/// and reads what the target runs (<c>GET /Definitions/latest</c>) and has pending
/// (<c>GET /Deployments/requests</c>) for the environments board. These are the Admin UI's only
/// HTTP calls: it talks to its own cluster through grains, but another cluster only offers its
/// REST API.
/// </summary>
public sealed partial class EnvironmentClient(
    IHttpClientFactory httpClientFactory,
    IOptions<EnvironmentOptions> environment,
    ILogger<EnvironmentClient> logger)
{
    public const string HttpClientName = "fleans-environments";

    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public IReadOnlyList<PromotionTarget> Targets => environment.Value.PromotionTargets;

    /// <param name="promotedBy">The Admin UI user; sent as <c>RequestedBy</c> unless the target
    /// takes a client-credentials token, whose user is then the requester.</param>
    public async Task<DeploymentRequestDto> PromoteAsync(string targetName, string bpmnXml, string promotedBy,
        string? comment, bool deployDisabled, CancellationToken ct = default)
    {
        var target = FindTarget(targetName);
        LogPromoting(target.Name, promotedBy);
        var content = JsonContent.Create(new SubmitDeploymentRequest(
            bpmnXml, comment, deployDisabled,
            RequestedBy: target.UsesClientCredentials ? null : promotedBy));
        return await SendAsync<DeploymentRequestDto>(target, HttpMethod.Post, "Deployments/requests", content, ct);
    }

    /// <summary>The target's latest version of each of <paramref name="processDefinitionKeys"/> it has.</summary>
    public Task<List<ProcessVersionDto>> GetLatestVersionsAsync(string targetName,
        IReadOnlyCollection<string> processDefinitionKeys, CancellationToken ct = default)
    {
        var query = string.Join("&", processDefinitionKeys.Select(k => "keys=" + Uri.EscapeDataString(k)));
        return SendAsync<List<ProcessVersionDto>>(FindTarget(targetName), HttpMethod.Get, "Definitions/latest?" + query, null, ct);
    }

    /// <summary>The target's pending deployment requests.</summary>
    public Task<List<DeploymentRequestDto>> GetPendingRequestsAsync(string targetName, CancellationToken ct = default) =>
        SendAsync<List<DeploymentRequestDto>>(FindTarget(targetName), HttpMethod.Get, "Deployments/requests?status=Pending", null, ct);

    private PromotionTarget FindTarget(string targetName) =>
        Targets.FirstOrDefault(t => t.Name == targetName)
            ?? throw new ArgumentException($"Unknown promotion target '{targetName}'.", nameof(targetName));

    private async Task<T> SendAsync<T>(PromotionTarget target, HttpMethod method, string path,
        HttpContent? content, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(method, new Uri(new Uri(AppendSlash(target.ApiUrl)), path))
        {
            Content = content,
        };
        if (target.UsesClientCredentials)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(target, client, ct));

        using var response = await client.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorAsync(response, ct);
            LogRequestFailed(target.Name, method.Method, path, (int)response.StatusCode, detail);
            throw new InvalidOperationException($"{target.Name} answered {(int)response.StatusCode}: {detail}");
        }

        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new InvalidOperationException($"{target.Name} returned an empty response.");
    }

    private async Task<string> GetTokenAsync(PromotionTarget target, HttpClient client, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(target.Name, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached.Token;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = target.ClientId!,
                ["client_secret"] = target.ClientSecret ?? "",
            };
            if (!string.IsNullOrWhiteSpace(target.Scope))
                form["scope"] = target.Scope;

            using var response = await client.PostAsync(target.TokenUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Token request for {target.Name} failed with {(int)response.StatusCode}.");

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException($"Token endpoint of {target.Name} returned no token.");
            // Renew a minute early so a token never expires mid-request.
            var lifetime = TimeSpan.FromSeconds(Math.Max(token.ExpiresIn - 60, 0));
            _tokens[target.Name] = (token.AccessToken, DateTimeOffset.UtcNow + lifetime);
            return token.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            foreach (var name in new[] { "error", "detail", "title" })
                if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString()!;
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "no details" : body;
    }

    private static string AppendSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    [LoggerMessage(EventId = 13010, Level = LogLevel.Information,
        Message = "Promoting a version to environment {Target} for {PromotedBy}")]
    private partial void LogPromoting(string target, string promotedBy);

    [LoggerMessage(EventId = 13011, Level = LogLevel.Warning,
        Message = "{Method} {Path} on environment {Target} failed with HTTP {StatusCode}: {Detail}")]
    private partial void LogRequestFailed(string target, string method, string path, int statusCode, string detail);
}
