using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Mints access tokens from the E2E Keycloak realm (<c>Fleans.Aspire/e2e-auth/fleans-realm.json</c>)
/// via the resource-owner password grant. Test-only: the realm enables direct access grants on
/// the <c>fleans-e2e*</c> clients purely so specs can obtain user tokens without a browser.
/// </summary>
internal static class KeycloakTokenClient
{
    /// <summary>Client whose tokens carry <c>aud=fleans-api</c> and the <c>groups</c> claim.</summary>
    public const string ApiClientId = "fleans-e2e";
    public const string ApiClientSecret = "fleans-e2e-secret";

    /// <summary>Client whose tokens carry the <c>groups</c> claim but NOT <c>aud=fleans-api</c>.</summary>
    public const string OtherClientId = "fleans-e2e-other";
    public const string OtherClientSecret = "fleans-e2e-other-secret";

    private static readonly HttpClient Http = new();

    public static Task<string> GetApiTokenAsync(string username, string password) =>
        GetTokenAsync(ApiClientId, ApiClientSecret, username, password);

    public static async Task<string> GetTokenAsync(
        string clientId, string clientSecret, string username, string password)
    {
        var keycloak = AspireFixture.KeycloakBaseUri
            ?? throw new InvalidOperationException("Keycloak is not provisioned (FLEANS_E2E_AUTH != true).");
        var tokenEndpoint = new Uri(keycloak, "/realms/fleans/protocol/openid-connect/token");

        using var response = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid",
        }));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Keycloak token request for client '{clientId}' failed: {(int)response.StatusCode} {body}");
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Keycloak returned no access_token.");
    }

    /// <summary>Decodes (without validating) the JWT payload — used to sanity-check test fixtures.</summary>
    public static JsonElement DecodePayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return doc.RootElement.Clone();
    }

    /// <summary>An HttpClient against Fleans.Api carrying <paramref name="accessToken"/> as a bearer token.</summary>
    public static HttpClient CreateApiClient(string? accessToken)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        // HTTPS endpoint directly: HttpClient strips the Authorization header when it follows the
        // Api's HTTP→HTTPS redirect, so a bearer request against the http endpoint would 401.
        var client = new HttpClient(handler) { BaseAddress = AspireFixture.ApiHttpsBaseUri };
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return client;
    }
}
