using Aspire.Hosting;
using Aspire.Hosting.Testing;
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

    public static Uri ApiBaseUri { get; private set; } = null!;

    public static Uri WebBaseUri { get; private set; } = null!;

    public static HttpClient ApiHttpClient { get; private set; } = null!;

    public static IBrowser Browser =>
        _browser ?? throw new InvalidOperationException("AspireFixture is not initialised.");

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        // Force the lightest dev defaults so the suite boots on stock CI runners:
        //   - Sqlite persistence (no Postgres container)
        //   - Combined silo role (Api hosts both Core + Worker grains in one process)
        // Redis is still required because the AppHost wires it unconditionally for
        // clustering + PubSubStore + the default Redis stream provider. CI runners
        // (ubuntu-latest) ship Docker preinstalled, which is sufficient.
        Environment.SetEnvironmentVariable("FLEANS_PERSISTENCE_PROVIDER", "Sqlite");

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Fleans_Aspire>();
        _application = await builder.BuildAsync();
        await _application.StartAsync();

        ApiBaseUri = _application.GetEndpoint("fleans-core");
        WebBaseUri = _application.GetEndpoint("fleans-management");
        ApiHttpClient = _application.CreateHttpClient("fleans-core");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = PlaywrightSettings.Headless,
        });
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
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
