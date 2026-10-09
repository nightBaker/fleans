using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// E2E tests share a single in-process Aspire stack + single Playwright Browser; running
// specs in parallel would race over the same Web/Api endpoints and SQLite DB. Keep all
// tests serial in this assembly.
[assembly: DoNotParallelize]

namespace Fleans.E2E.Tests.Infrastructure;

[TestClass]
public static class AspireFixture
{
    private static DistributedApplication? _application;
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    private static TestHttpServer? _testHttpServer;

    public static Uri ApiBaseUri { get; private set; } = null!;

    public static Uri WebBaseUri { get; private set; } = null!;

    /// <summary>
    /// True when the AppHost was booted with <c>FLEANS_E2E_AUTH=true</c> (#771): Keycloak is
    /// provisioned, Fleans.Api enforces JWT bearer and Fleans.Web enforces OIDC login. Only
    /// <see cref="E2ECategories.Auth"/> specs are meaningful in that mode.
    /// </summary>
    public static bool AuthEnabled { get; private set; }

    /// <summary>Host-side Keycloak base URL when <see cref="AuthEnabled"/>; otherwise null.</summary>
    public static Uri? KeycloakBaseUri { get; private set; }

    /// <summary>
    /// Fleans.Api HTTPS endpoint, resolved only when <see cref="AuthEnabled"/>. Bearer requests
    /// must target it directly — HttpClient drops the Authorization header on redirects.
    /// </summary>
    public static Uri ApiHttpsBaseUri { get; private set; } = null!;

    public static HttpClient ApiHttpClient { get; private set; } = null!;

    public static IBrowser Browser =>
        _browser ?? throw new InvalidOperationException("AspireFixture is not initialised.");

    /// <summary>
    /// Base URL of an in-process HTTP echo server reachable from the Fleans.Api process
    /// (RestCaller plugin handler hits this from inside the workflow). Bound to a random
    /// localhost port; serves <c>GET /echo</c> and <c>GET /status/{code}</c>.
    /// </summary>
    public static string TestHttpServerBaseUrl =>
        _testHttpServer?.BaseUrl ?? throw new InvalidOperationException("AspireFixture is not initialised.");

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        // Default to the lightest dev topology so the suite boots on stock CI runners:
        //   - Sqlite persistence (no Postgres container)
        //   - Redis streaming (reuses the Redis the AppHost wires unconditionally for
        //     clustering + PubSubStore)
        //   - Combined silo role (Api hosts both Core + Worker grains in one process)
        // FLEANS_PERSISTENCE_PROVIDER / FLEANS_STREAMING_PROVIDER, when already set, are
        // passed through to the AppHost unchanged — CI's e2e-providers job uses this to
        // re-run the E2E-Smoke subset on Postgres, Kafka and Azure Queue (Azurite).
        // FLEANS_E2E_AUTH=true (the e2e-auth job) is passed through the same way and makes the
        // AppHost provision Keycloak and wire JWT/OIDC into Api and Web (#771).
        // CI runners (ubuntu-latest) ship Docker preinstalled, which is sufficient.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLEANS_PERSISTENCE_PROVIDER")))
        {
            Environment.SetEnvironmentVariable("FLEANS_PERSISTENCE_PROVIDER", "Sqlite");
        }

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Fleans_Aspire>();
        _application = await builder.BuildAsync();
        AssertRequestedProvidersProvisioned(_application);
        await _application.StartAsync();

        // Use HTTP endpoint as the base — Fleans.Api/Web call UseHttpsRedirection() so HTTP
        // requests redirect (307) to HTTPS. The HttpClient follows the redirect with the
        // same handler, which is configured below to bypass TLS validation. The redirected
        // HTTPS URL uses the ASP.NET Core dev cert that isn't trusted on Linux CI runners
        // (UntrustedRoot); bypassing validation is safe because this is a local-only test
        // cluster with no production exposure.
        AuthEnabled = string.Equals(
            Environment.GetEnvironmentVariable("FLEANS_E2E_AUTH"), "true", StringComparison.OrdinalIgnoreCase);
        if (AuthEnabled)
        {
            KeycloakBaseUri = _application.GetEndpoint("keycloak", "http");
            ApiHttpsBaseUri = _application.GetEndpoint("fleans-core", "https");
        }

        ApiBaseUri = _application.GetEndpoint("fleans-core", "http");
        WebBaseUri = _application.GetEndpoint("fleans-management", "http");

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        ApiHttpClient = new HttpClient(handler) { BaseAddress = ApiBaseUri };

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = PlaywrightSettings.Headless,
        });

        _testHttpServer = TestHttpServer.Start();
    }

    /// <summary>
    /// Guards the provider legs against silently testing the default topology: if a
    /// provider override is requested but the AppHost didn't provision its backing resource
    /// (e.g. the env var name drifted), fail the run instead of going green on Sqlite/Redis.
    /// </summary>
    private static void AssertRequestedProvidersProvisioned(DistributedApplication app)
    {
        var resourceNames = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        void Require(string envVar, string value, string resourceName)
        {
            if (string.Equals(Environment.GetEnvironmentVariable(envVar), value, StringComparison.OrdinalIgnoreCase)
                && !resourceNames.Contains(resourceName))
            {
                throw new InvalidOperationException(
                    $"{envVar}={value} was requested but the AppHost has no '{resourceName}' resource. " +
                    $"Resources: [{string.Join(", ", resourceNames.Order())}].");
            }
        }

        Require("FLEANS_PERSISTENCE_PROVIDER", "Postgres", "postgres");
        Require("FLEANS_STREAMING_PROVIDER", "Kafka", "fleans-kafka");
        Require("FLEANS_STREAMING_PROVIDER", "AzureQueue", "fleans-azurite");
        Require("FLEANS_E2E_AUTH", "true", "keycloak");
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        _testHttpServer?.Dispose();
        _testHttpServer = null;

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;

        if (_application is not null)
        {
            await _application.DisposeAsync();
            _application = null;
        }
    }
}
