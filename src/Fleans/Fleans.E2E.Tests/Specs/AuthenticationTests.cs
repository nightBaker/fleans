using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/28-api-auth (Scenario B), tests/manual/30-web-auth (Scenario 2) and the
// JWT-groups half of tests/manual/61-usertask-group-claim (#771). Needs FLEANS_E2E_AUTH=true:
// the AppHost then provisions Keycloak with Fleans.Aspire/e2e-auth/fleans-realm.json
// (alice ∈ managers, bob ∈ no group) and wires Api (JWT bearer) + Web (OIDC) to it.
[TestClass]
[TestCategory("E2E")]
[TestCategory(E2ECategories.Auth)]
public class AuthenticationTests : WorkflowE2ETestBase
{
    [TestInitialize]
    public void RequireAuthTopology()
    {
        if (!AspireFixture.AuthEnabled)
        {
            Assert.Inconclusive("E2E-Auth specs need FLEANS_E2E_AUTH=true (Keycloak + JWT/OIDC topology).");
        }
    }

    [TestMethod]
    public async Task Api_WithoutToken_Returns401_HealthStaysAnonymous()
    {
        using var anonymous = KeycloakTokenClient.CreateApiClient(accessToken: null);

        using (var tasks = await anonymous.GetAsync("/UserTasks"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, tasks.StatusCode);
        }
        using (var deploy = await new FleansApiClient(anonymous).DisableAsync("does-not-matter"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, deploy.StatusCode);
        }
        using (var garbage = KeycloakTokenClient.CreateApiClient("not-a-jwt"))
        using (var withGarbage = await garbage.GetAsync("/UserTasks"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, withGarbage.StatusCode);
        }

        using var health = await anonymous.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "/health must stay anonymous under JWT auth.");
    }

    [TestMethod]
    public async Task Api_WithValidToken_Returns200()
    {
        var token = await KeycloakTokenClient.GetApiTokenAsync("alice", "alice");
        using var http = KeycloakTokenClient.CreateApiClient(token);

        using var tasks = await http.GetAsync("/UserTasks");
        Assert.AreEqual(HttpStatusCode.OK, tasks.StatusCode);

        var api = new FleansApiClient(http);
        var deployed = await api.DeployAsync(BpmnFixtureLoader.Load("61-usertask-group-claim", "group-claim.bpmn"));
        Assert.AreEqual("group-claim", deployed.ProcessDefinitionKey);
    }

    [TestMethod]
    public async Task Api_WithWrongAudienceToken_Returns401()
    {
        var token = await KeycloakTokenClient.GetTokenAsync(
            KeycloakTokenClient.OtherClientId, KeycloakTokenClient.OtherClientSecret, "alice", "alice");

        // Fixture sanity: the token is otherwise genuine (same issuer, signed by the realm) —
        // only the audience differs, so a 401 isolates audience validation.
        var payload = KeycloakTokenClient.DecodePayload(token);
        Assert.IsFalse(AudienceContains(payload, "fleans-api"), "fleans-e2e-other tokens must not carry aud=fleans-api.");

        using var http = KeycloakTokenClient.CreateApiClient(token);
        using var tasks = await http.GetAsync("/UserTasks");
        Assert.AreEqual(HttpStatusCode.Unauthorized, tasks.StatusCode);
    }

    [TestMethod]
    public async Task UserTaskClaim_UsesJwtGroups_BodySuppliedGroupsIgnored()
    {
        var aliceToken = await KeycloakTokenClient.GetApiTokenAsync("alice", "alice");
        var bobToken = await KeycloakTokenClient.GetApiTokenAsync("bob", "bob");
        using var aliceHttp = KeycloakTokenClient.CreateApiClient(aliceToken);
        using var bobHttp = KeycloakTokenClient.CreateApiClient(bobToken);
        var alice = new FleansApiClient(aliceHttp);
        var bob = new FleansApiClient(bobHttp);

        // Fixture sanity: alice's token carries groups=[managers], bob's carries none.
        Assert.IsTrue(GroupsOf(KeycloakTokenClient.DecodePayload(aliceToken)).Contains("managers"));
        Assert.IsFalse(GroupsOf(KeycloakTokenClient.DecodePayload(bobToken)).Contains("managers"));

        var deployed = await alice.DeployAsync(BpmnFixtureLoader.Load("61-usertask-group-claim", "group-claim.bpmn"));
        var started = await alice.StartAsync(deployed.ProcessDefinitionKey);
        var state = await alice.WaitForStateAsync(
            started.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("ApproveTask"));
        var taskId = state.ActiveActivities.First(a => a.ActivityId == "ApproveTask").ActivityInstanceId;

        // Anti-spoofing: bob's token has no group, but he claims membership in the body.
        // Under JWT the body is ignored, so the claim must be rejected.
        using (var spoofed = await bob.ClaimUserTaskAsync(taskId, "bob", ["managers"]))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, spoofed.StatusCode,
                "Body-supplied groups must be ignored when JWT auth is enabled.");
        }
        var afterSpoof = await alice.GetUserTaskAsync(taskId);
        Assert.IsNotNull(afterSpoof);
        Assert.IsNull(afterSpoof.ClaimedBy, "Rejected claim must not change the task.");

        // alice's token carries groups=[managers]; she sends no body groups at all, so the
        // successful claim proves the groups were resolved from the JWT.
        using (var claim = await alice.ClaimUserTaskAsync(taskId, "alice", userGroups: null))
        {
            Assert.IsTrue(claim.IsSuccessStatusCode,
                $"Claim with groups from the JWT should succeed; got {(int)claim.StatusCode} {await claim.Content.ReadAsStringAsync()}.");
        }
        var claimed = await alice.GetUserTaskAsync(taskId);
        Assert.IsNotNull(claimed);
        Assert.AreEqual("alice", claimed.ClaimedBy);
    }

    [TestMethod]
    public async Task Web_RedirectsToIssuer_LoginEstablishesSession()
    {
        var keycloak = AspireFixture.KeycloakBaseUri!;

        await Page.GotoAsync("/workflows");

        // Anonymous visit → OIDC challenge → Keycloak login page for the fleans realm.
        // Keycloak may bounce /protocol/openid-connect/auth → /login-actions/authenticate before
        // rendering the form, so match on the realm prefix and then wait for the form itself.
        await WaitForUrlOrExplainAsync(
            new Regex("^" + Regex.Escape($"{keycloak.Scheme}://{keycloak.Authority}/realms/fleans/")));
        Assert.Contains("client_id=fleans-web", Page.Url);
        await Assertions.Expect(Page.Locator("#username")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        await Page.FillAsync("#username", "alice");
        await Page.FillAsync("#password", "alice");
        await Page.ClickAsync("#kc-login");

        // Back through /signin-oidc to the deep link, authenticated.
        await WaitForUrlOrExplainAsync(new Regex(@"^https://[^/]+/workflows$"));
        await Assertions.Expect(Page.GetByText("Signed in as alice")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // The session cookie carries across navigations — no second trip to the issuer.
        await Page.GotoAsync("/");
        await Assertions.Expect(Page.GetByText("Signed in as alice")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        Assert.DoesNotStartWith($"{keycloak.Scheme}://{keycloak.Authority}", Page.Url);
    }

    /// <summary>WaitForURL that reports where the browser actually ended up on timeout.</summary>
    private async Task WaitForUrlOrExplainAsync(Regex expected)
    {
        try
        {
            await Page.WaitForURLAsync(expected, new PageWaitForURLOptions { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            var body = await Page.InnerTextAsync("body");
            Assert.Fail($"Expected URL matching {expected} but browser is at {Page.Url}. Page text: {body[..Math.Min(body.Length, 500)]}");
        }
    }

    private static bool AudienceContains(JsonElement payload, string audience)
    {
        if (!payload.TryGetProperty("aud", out var aud)) return false;
        return aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Any(a => a.GetString() == audience)
            : aud.GetString() == audience;
    }

    private static List<string> GroupsOf(JsonElement payload) =>
        payload.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array
            ? groups.EnumerateArray().Select(g => g.GetString()!).ToList()
            : [];
}
