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
        // CI runners (ubuntu-latest) ship Docker preinstalled, which is sufficient.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLEANS_PERSISTENCE_PROVIDER")))
        {
            Environment.SetEnvironmentVariable("FLEANS_PERSISTENCE_PROVIDER", "Sqlite");
        }

        // Split-roles leg (#770): FLEANS_SPLIT_ROLES=true makes the AppHost run fleans-core as
        // Core plus a dedicated fleans-worker silo; we also hand it the test-only Plugin-role
        // host (Fleans.E2E.PluginHost) and register two deliberately mis-roled silos that the
        // role-mismatch spec starts on demand.
        IsSplitRoles = string.Equals(
            Environment.GetEnvironmentVariable(SplitRolesEnvVar), "true", StringComparison.OrdinalIgnoreCase);
        if (IsSplitRoles && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLEANS_PLUGIN_HOST_PROJECT")))
        {
            Environment.SetEnvironmentVariable("FLEANS_PLUGIN_HOST_PROJECT", SourceProjectPath("Fleans.E2E.PluginHost"));
        }

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Fleans_Aspire>();
        if (IsSplitRoles)
        {
            AddMisroledSilos(builder);
        }
        _application = await builder.BuildAsync();
        AssertRequestedProvidersProvisioned(_application);
        if (IsSplitRoles)
        {
            StartResourceLogCapture(_application);
        }
        await _application.StartAsync();

        if (IsSplitRoles)
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            foreach (var resource in new[] { WorkerResource, PluginHostResource })
            {
                await _application.ResourceNotifications.WaitForResourceAsync(
                    resource, KnownResourceStates.Running, startup.Token);
            }
        }

        // Use HTTP endpoint as the base — Fleans.Api/Web call UseHttpsRedirection() so HTTP
        // requests redirect (307) to HTTPS. The HttpClient follows the redirect with the
        // same handler, which is configured below to bypass TLS validation. The redirected
        // HTTPS URL uses the ASP.NET Core dev cert that isn't trusted on Linux CI runners
        // (UntrustedRoot); bypassing validation is safe because this is a local-only test
        // cluster with no production exposure.
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
        Require(SplitRolesEnvVar, "true", WorkerResource);
        Require(SplitRolesEnvVar, "true", PluginHostResource);
    }

    public const string SplitRolesEnvVar = "FLEANS_SPLIT_ROLES";
    public const string WorkerResource = "fleans-worker";
    public const string PluginHostResource = "fleans-plugin-host";

    /// <summary>Fleans.WorkerHost started with <c>Fleans:Role=Plugin</c> (explicit start only).</summary>
    public const string MisroledWorkerResource = "misroled-worker";

    /// <summary>Fleans.E2E.PluginHost started with <c>Fleans:Role=Worker</c> (explicit start only).</summary>
    public const string MisroledPluginHostResource = "misroled-plugin-host";

    /// <summary>True when the stack runs the Core + Worker + Plugin split topology.</summary>
    public static bool IsSplitRoles { get; private set; }

    public static DistributedApplication Application =>
        _application ?? throw new InvalidOperationException("AspireFixture is not initialised.");

    /// <summary>
    /// Registers two silos whose <c>Fleans:Role</c> contradicts their host — the role-mismatch
    /// spec starts them and asserts each one refuses to boot. <c>WithExplicitStart</c> keeps
    /// them out of the normal startup so they never touch the shared cluster.
    /// </summary>
    private static void AddMisroledSilos(IDistributedApplicationTestingBuilder builder)
    {
        var redis = builder.CreateResourceBuilder(
            builder.Resources.OfType<RedisResource>().Single(r => r.Name == "orleans-redis"));

        builder.AddProject(MisroledWorkerResource, SourceProjectPath("Fleans.WorkerHost"))
            .WithReference(redis)
            .WithEnvironment("Fleans__Role", "Plugin")
            .WithExplicitStart();

        builder.AddProject(MisroledPluginHostResource, SourceProjectPath("Fleans.E2E.PluginHost"))
            .WithReference(redis)
            .WithEnvironment("Fleans__Role", "Worker")
            .WithExplicitStart();
    }

    /// <summary>
    /// Directory holding one <c>{resourceId}.log</c> per Aspire resource instance (split-roles
    /// leg only). Multi-silo failures are undiagnosable from the test output alone; CI uploads
    /// this directory as an artifact on failure.
    /// </summary>
    public static string ResourceLogDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "resource-logs");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _capturedResources = new();

    private static void StartResourceLogCapture(DistributedApplication app)
    {
        if (Directory.Exists(ResourceLogDirectory))
        {
            Directory.Delete(ResourceLogDirectory, recursive: true);
        }
        Directory.CreateDirectory(ResourceLogDirectory);
        var loggers = app.Services.GetRequiredService<ResourceLoggerService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in app.ResourceNotifications.WatchAsync())
                {
                    if (!_capturedResources.TryAdd(evt.ResourceId, 0)) continue;
                    var resourceId = evt.ResourceId;
                    _ = Task.Run(async () =>
                    {
                        var path = Path.Combine(ResourceLogDirectory, resourceId + ".log");
                        try
                        {
                            await foreach (var batch in loggers.WatchAsync(resourceId))
                            {
                                await File.AppendAllLinesAsync(path, batch.Select(l => l.Content));
                            }
                        }
                        catch (Exception)
                        {
                            // Best-effort diagnostics; never fail the run over log capture.
                        }
                    });
                }
            }
            catch (Exception)
            {
                // App disposed.
            }
        });
    }

    /// <summary>Captured log text for every instance of <paramref name="resourceName"/>.</summary>
    public static string ReadCapturedLogs(string resourceName)
    {
        if (!Directory.Exists(ResourceLogDirectory)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(ResourceLogDirectory, resourceName + "*.log"))
        {
            try { sb.Append(File.ReadAllText(file)); } catch (IOException) { }
        }
        return sb.ToString();
    }

    private static string SourceProjectPath(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, projectName, projectName + ".csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate {projectName}.csproj above '{AppContext.BaseDirectory}'.");
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
