using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.Extensions.Options;

namespace Fleans.Web.Services;

/// <summary>
/// Promotes a version to another environment (#900) by submitting its BPMN XML to the target's
/// <c>POST /Deployments/requests</c>. This is the Admin UI's one HTTP call: it talks to its own
/// cluster through grains, but another cluster only offers its REST API.
/// </summary>
public sealed partial class PromotionClient(
    IHttpClientFactory httpClientFactory,
    IOptions<EnvironmentOptions> environment,
    ILogger<PromotionClient> logger)
{
    public const string HttpClientName = "fleans-promotion";

    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public IReadOnlyList<PromotionTarget> Targets => environment.Value.PromotionTargets;

    /// <param name="promotedBy">The Admin UI user; sent as <c>RequestedBy</c> unless the target
    /// takes a client-credentials token, whose user is then the requester.</param>
    public async Task<DeploymentRequestDto> PromoteAsync(string targetName, string bpmnXml, string promotedBy,
        string? comment, bool deployDisabled, CancellationToken ct = default)
    {
        var target = Targets.FirstOrDefault(t => t.Name == targetName)
            ?? throw new ArgumentException($"Unknown promotion target '{targetName}'.", nameof(targetName));

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(AppendSlash(target.ApiUrl)), "Deployments/requests"))
        {
            Content = JsonContent.Create(new SubmitDeploymentRequest(
                bpmnXml, comment, deployDisabled,
                RequestedBy: target.UsesClientCredentials ? null : promotedBy)),
        };
        if (target.UsesClientCredentials)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(target, client, ct));

        LogPromoting(target.Name, promotedBy);
        using var response = await client.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorAsync(response, ct);
            LogPromotionFailed(target.Name, (int)response.StatusCode, detail);
            throw new InvalidOperationException($"{target.Name} answered {(int)response.StatusCode}: {detail}");
        }

        return await response.Content.ReadFromJsonAsync<DeploymentRequestDto>(ct)
            ?? throw new InvalidOperationException($"{target.Name} returned no deployment request.");
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
        Message = "Promotion to environment {Target} failed with HTTP {StatusCode}: {Detail}")]
    private partial void LogPromotionFailed(string target, int statusCode, string detail);
}
