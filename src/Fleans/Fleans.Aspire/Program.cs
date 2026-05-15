using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Publish target selection — only ONE compute environment may be registered at a time; if both
// AddKubernetesEnvironment and AddDockerComposeEnvironment are called, Aspire requires every
// resource to call WithComputeEnvironment(...) to disambiguate, which is impractical.
//
// The release pipeline's `compose` job runs `aspire publish -t docker-compose -o out/compose`.
// The `helm-package` job uses hand-written charts (helm package charts/fleans), NOT aspire
// publish -t kubernetes, so the k8s environment is only needed for ad-hoc `aspire publish
// -t kubernetes` runs (e.g. debugging manifest shape locally).
//
// Set ASPIRE_PUBLISH_ENV=kubernetes to get k8s manifests; default is compose.
// In dev mode (dotnet run) neither registration fires — both are publish-only no-ops.
var publishEnv = builder.Configuration["ASPIRE_PUBLISH_ENV"] ?? "compose";
if (publishEnv.Equals("compose", StringComparison.OrdinalIgnoreCase))
{
    // Docker Compose publish target — `aspire publish -t docker-compose -o out/compose` emits a
    // Compose Spec YAML referencing every Aspire-hosted service plus its dependencies. Required
    // for the release pipeline's `compose` job; without this call, `aspire publish -t docker-compose`
    // silently routes to whichever publisher IS registered (Kubernetes here) and produces unusable
    // output. See tests/manual/42-release-pipeline/test-plan.md Pitfall #8.
    builder.AddDockerComposeEnvironment("compose");
}
else if (publishEnv.Equals("kubernetes", StringComparison.OrdinalIgnoreCase))
{
    // Kubernetes publish target — `aspire publish -t kubernetes -o out/k8s` emits manifests for
    // every Aspire-hosted service plus its dependencies (Redis, optional PostgreSQL/Kafka). The
    // "k8s" name is the resource id in the AppHost model; the publish target type is "kubernetes".
    builder.AddKubernetesEnvironment("k8s");
}
else
{
    throw new InvalidOperationException(
        $"Unknown ASPIRE_PUBLISH_ENV value '{publishEnv}'. Valid values: 'compose' (default), 'kubernetes'.");
}

// Persistence provider — set FLEANS_PERSISTENCE_PROVIDER=Postgres / Sqlite to override.
// Defaults: Sqlite in dev (fast local dev, no container required); Postgres in publish mode
// (release-asset compose bundle and Helm chart both target Postgres — SQLite without a shared
// volume cannot back a multi-silo deployment, see fix/compose-bundle-defects).
var defaultPersistenceProvider = builder.ExecutionContext.IsPublishMode ? "Postgres" : "Sqlite";
var persistenceProvider = builder.Configuration["FLEANS_PERSISTENCE_PROVIDER"] ?? defaultPersistenceProvider;
var usePostgres = persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);

// Streaming provider — defaults to Redis (durable, reuses the existing orleans-redis container).
// Override with FLEANS_STREAMING_PROVIDER=Memory (in-process, single-silo only — debug-only),
// FLEANS_STREAMING_PROVIDER=Kafka (separate Kafka cluster), or FLEANS_STREAMING_PROVIDER=AzureQueue (Azurite/Azure Storage).
var streamingProvider = builder.Configuration["FLEANS_STREAMING_PROVIDER"] ?? "Redis";
var useKafka = streamingProvider.Equals("Kafka", StringComparison.OrdinalIgnoreCase);
var useAzureQueue = streamingProvider.Equals("AzureQueue", StringComparison.OrdinalIgnoreCase);
var useRedisStreaming = streamingProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase);

// Add Redis for Orleans clustering and storage.
// Aspire 13.1+ auto-configures TLS for Redis containers, but the Orleans Redis
// provider doesn't negotiate TLS. Disable to avoid health check failures (dotnet/aspire#13612).
var redis = builder.AddRedis("orleans-redis").WithoutHttpsCertificate();

// Authentication parameters (D2a) — declared unconditionally with empty defaults so the
// AppHost contract is stable. When the operator does not supply values at run-time, the
// downstream Web project sees empty strings for Authority/ClientId and falls into
// auth-disabled mode (single source of truth lives in Fleans.Web/Program.cs).
var authAuthority = builder.AddParameter("auth-authority", () => "");
var authClientId = builder.AddParameter("auth-client-id", () => "");
var authClientSecret = builder.AddParameter("auth-client-secret", () => "", secret: true);

// Centralized Orleans configuration
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryReminders();

IResourceBuilder<PostgresDatabaseResource>? pg = null;
string? sqliteConnectionString = null;

if (usePostgres)
{
    pg = builder.AddPostgres("postgres")
        .WithDataVolume()
        .AddDatabase("fleans");
}
else
{
    var sqliteDbPath = Path.Combine(Path.GetTempPath(), "fleans-dev.db");
    sqliteConnectionString = $"DataSource={sqliteDbPath}";
}

// Kafka resource — only provisioned when streaming provider is Kafka. Default Memory mode
// boots with no Kafka container (parity with the persistence Sqlite default).
IResourceBuilder<KafkaServerResource>? kafka = null;
if (useKafka)
{
    kafka = builder.AddKafka("fleans-kafka").WithKafkaUI();
}

// Azure Queue resource — only provisioned when streaming provider is AzureQueue.
// In dev mode, Aspire auto-provisions Azurite (the Azure Storage emulator) so no
// real Azure subscription is needed. In production, set AccountName for Managed Identity.
IResourceBuilder<AzureQueueStorageResource>? azureQueues = null;
if (useAzureQueue)
{
    var azurite = builder.AddAzureStorage("fleans-azurite").RunAsEmulator();
    azureQueues = azurite.AddQueues("fleans-queues");
}

// Local helper: wires persistence env-vars / resource references onto a project.
// WaitFor(pg) is intentionally omitted — startup ordering is the caller's responsibility.
// Web and Mcp already wait for fleansSilo, which waits for pg when using Postgres.
static IResourceBuilder<ProjectResource> WithPersistence(
    IResourceBuilder<ProjectResource> project,
    bool isPostgres,
    IResourceBuilder<PostgresDatabaseResource>? pgDb,
    string? sqliteConn)
{
    if (isPostgres)
    {
        return project
            .WithReference(pgDb!)
            .WithEnvironment("Persistence__Provider", "Postgres");
    }
    return project
        .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConn!)
        .WithEnvironment("FLEANS_QUERY_CONNECTION", sqliteConn!);
}

// Local helper: wires streaming env-vars / resource references onto a silo project.
// Redis provider is the default — reuses the existing orleans-redis container (no new
// infrastructure). Memory falls back when FLEANS_STREAMING_PROVIDER=Memory is set (Fleans.ServiceDefaults
// reads `Fleans:Streaming:Provider`, so injecting "Redis" explicitly here ensures the publish
// output carries the same default as dev mode).
static IResourceBuilder<ProjectResource> WithStreaming(
    IResourceBuilder<ProjectResource> project,
    bool isKafka,
    IResourceBuilder<KafkaServerResource>? kafkaResource,
    bool isAzureQueue,
    IResourceBuilder<AzureQueueStorageResource>? azureQueuesResource,
    bool isRedis = false)
{
    if (isKafka)
    {
        return project
            .WithReference(kafkaResource!)
            .WaitFor(kafkaResource!)
            .WithEnvironment("Fleans__Streaming__Provider", "Kafka")
            .WithEnvironment("Fleans__Streaming__Kafka__Brokers", kafkaResource!.Resource.ConnectionStringExpression);
    }
    if (isAzureQueue)
    {
        return project
            .WithReference(azureQueuesResource!)
            .WaitFor(azureQueuesResource!)
            .WithEnvironment("Fleans__Streaming__Provider", "AzureQueue")
            .WithEnvironment("Fleans__Streaming__AzureQueue__ConnectionString", azureQueuesResource!.Resource.ConnectionStringExpression);
    }
    if (isRedis)
    {
        // Redis is already wired as the keyed orleans-redis client via WithReference(orleans) /
        // AddKeyedRedisClient. FleanStreamingExtensions.AddRedisStreams aliases it to a non-keyed
        // IConnectionMultiplexer for the third-party stream provider. No new env-vars needed —
        // the connection string lives in ConnectionStrings:orleans-redis.
        return project
            .WithEnvironment("Fleans__Streaming__Provider", "Redis");
    }
    return project;
}

// Api = Orleans silo
// Match the Helm chart's deployment-core.yaml: Aspire publish-mode tags fleans-core as
// Core (publish topology has a dedicated fleans-worker deployment for worker grains;
// operators wanting a custom-task plugin host run a separate silo built from
// github.com/nightBaker/fleans-custom-worker-example), while dev runs (3-process) stay
// at Combined so fleans-core continues to
// host worker grains. FLEANS_ROLE env override applies in either mode for local
// experimentation.
var defaultCoreRole = builder.ExecutionContext.IsPublishMode ? "Core" : "Combined";
var coreRole = builder.Configuration["FLEANS_ROLE"] ?? defaultCoreRole;

var apiProject = builder.AddProject<Projects.Fleans_Api>("fleans-core")
    .WithEnvironment("Fleans__Role", coreRole)
    .WithReference(orleans)
    .WaitFor(redis)
    .WithReplicas(1);
if (usePostgres) apiProject = apiProject.WaitFor(pg!);
// Mark the API endpoint as externally accessible so the docker-compose publisher emits a
// host port mapping (`ports:`) rather than internal-only `expose:`. Without this, the
// release-asset compose bundle is unreachable from the host even after `docker compose up`.
if (builder.ExecutionContext.IsPublishMode)
{
    apiProject = apiProject.WithExternalHttpEndpoints();
}
var fleansSilo = WithPersistence(apiProject, usePostgres, pg, sqliteConnectionString);
fleansSilo = WithStreaming(fleansSilo, useKafka, kafka, useAzureQueue, azureQueues, useRedisStreaming);

// Web = Orleans client
var webProject = builder.AddProject<Projects.Fleans_Web>("fleans-management")
    .WithReference(orleans.AsClient())
    .WaitFor(fleansSilo)
    .WithEnvironment("Authentication__Authority", authAuthority)
    .WithEnvironment("Authentication__ClientId", authClientId)
    .WithEnvironment("Authentication__ClientSecret", authClientSecret)
    .WithReplicas(1);
if (builder.ExecutionContext.IsPublishMode)
{
    webProject = webProject.WithExternalHttpEndpoints();
}
WithPersistence(webProject, usePostgres, pg, sqliteConnectionString);

// MCP = Orleans client (for Claude Code)
WithPersistence(
    builder.AddProject<Projects.Fleans_Mcp>("fleans-mcp")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithHttpEndpoint(port: 5200, name: "mcp")
        .WithReplicas(1),
    usePostgres, pg, sqliteConnectionString);

// Publish-only topology: registers the dedicated Fleans.WorkerHost silo (always) and,
// optionally, the load-test nginx fan-out (gated by FLEANS_LOAD_TEST_MODE=true). Local dev
// (`dotnet run --project Fleans.Aspire`) keeps the original 3-process layout — Fleans.Api
// with the default Combined role still hosts worker grains there.
//
// FLEANS_LOAD_TEST_MODE gates the nginx + 2-replica fan-out from end-user release
// artifacts. End-user `docker-compose-v<VERSION>.zip` and the hand-written helm chart
// should not contain a load-test nginx fronting 2 fleans-core replicas — that's a
// developer concern. The load-test runbook (tests/load/README.md) sets the flag to
// opt in.
//
// The guard also protects the (rare) `ASPIRE_PUBLISH_ENV=kubernetes` path: nginx uses
// WithBindMount(tests/load/nginx.conf, ...) which Aspire's Kubernetes publisher rejects
// (Bind mounts are not supported by the Kubernetes publisher). With the load-test mode
// off, the bind-mounted container is never registered.
var loadTestMode = string.Equals(
    builder.Configuration["FLEANS_LOAD_TEST_MODE"],
    "true",
    StringComparison.OrdinalIgnoreCase);

if (builder.ExecutionContext.IsPublishMode)
{
    // Worker silo — separate deployable so production/k8s topologies can scale Core and
    // Worker independently. Joins the same Orleans cluster via Redis clustering; worker grains
    // (script executor, condition evaluator, custom-task plugins) place onto it preferentially
    // via WorkerPlacementDirector.
    var workerHost = builder.AddProject<Projects.Fleans_WorkerHost>("fleans-worker")
        .WithReference(orleans)
        .WaitFor(redis)
        .WithEnvironment("Fleans__Role", "Worker")
        .WithReplicas(1);
    if (usePostgres) workerHost = workerHost.WaitFor(pg!);
    workerHost = WithPersistence(workerHost, usePostgres, pg, sqliteConnectionString);
    workerHost = WithStreaming(workerHost, useKafka, kafka, useAzureQueue, azureQueues, useRedisStreaming);

    // NOTE: the "host your own custom-task plugins" template is intentionally NOT
    // registered here. It lives in a separate repository as a GitHub template:
    // https://github.com/nightBaker/fleans-custom-worker-example
    // Click "Use this template" on that repo to scaffold your own plugin host.
    // Shipping it from this Aspire AppHost would force the release pipeline to
    // publish a fleans-custom-worker image alongside api/web/worker/mcp, which
    // it does not (release.yml's image matrix builds 4 images).

    // Load-test fan-out — opt-in via FLEANS_LOAD_TEST_MODE=true. Targets the docker-compose
    // publisher only; the K8s publisher rejects the bind mount and Kubernetes Services
    // already load-balance replicas natively, so the nginx fan-out is unnecessary there.
    if (loadTestMode)
    {
        // Override the container listen port to 8080 so nginx (tests/load/nginx.conf) can
        // reach each replica at fleans-core:8080. WithHttpEndpoint cannot be used here because
        // AddProject<> already registers the default "http" endpoint — re-using that name throws.
        apiProject = apiProject
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", "8080")
            .WithReplicas(2);

        builder.AddContainer("nginx", "nginx:1.27")
            .WithBindMount("../../tests/load/nginx.conf", "/etc/nginx/nginx.conf", isReadOnly: true)
            .WithHttpEndpoint(port: 80, targetPort: 80, name: "http")
            .WaitFor(apiProject);
    }
}

using var app = builder.Build();
await app.RunAsync();
