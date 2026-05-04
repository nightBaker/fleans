var builder = DistributedApplication.CreateBuilder(args);

// Kubernetes publish target — `aspire publish -t kubernetes -o out/k8s` emits manifests for
// every Aspire-hosted service plus its dependencies (Redis, optional PostgreSQL/Kafka). The
// "k8s" name is the resource id in the AppHost model; the publish target type is "kubernetes".
builder.AddKubernetesEnvironment("k8s");

// Persistence provider — set FLEANS_PERSISTENCE_PROVIDER=Postgres to use PostgreSQL.
// Default is SQLite (fast local dev, no container required).
var persistenceProvider = builder.Configuration["FLEANS_PERSISTENCE_PROVIDER"] ?? "Sqlite";
var usePostgres = persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);

// Streaming provider — set FLEANS_STREAMING_PROVIDER=Kafka to opt into Kafka-backed Orleans Streams.
// Default is in-memory (matches the v1 design — zero-infra `dotnet run --project Fleans.Aspire`).
var streamingProvider = builder.Configuration["FLEANS_STREAMING_PROVIDER"] ?? "Memory";
var useKafka = streamingProvider.Equals("Kafka", StringComparison.OrdinalIgnoreCase);

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

// Local helper: wires Kafka streaming env-vars / resource references onto a silo project.
// Memory provider is the default and requires no env-vars (Fleans.ServiceDefaults reads
// `Fleans:Streaming:Provider` and falls back to "memory" when unset).
static IResourceBuilder<ProjectResource> WithStreaming(
    IResourceBuilder<ProjectResource> project,
    bool isKafka,
    IResourceBuilder<KafkaServerResource>? kafkaResource)
{
    if (!isKafka)
    {
        return project;
    }
    return project
        .WithReference(kafkaResource!)
        .WaitFor(kafkaResource!)
        .WithEnvironment("Fleans__Streaming__Provider", "Kafka")
        .WithEnvironment("Fleans__Streaming__Kafka__Brokers", kafkaResource!.Resource.ConnectionStringExpression);
}

// Api = Orleans silo
// Match the Helm chart's deployment-core.yaml: Aspire publish-mode tags fleans-core as
// Core (publish topology has dedicated fleans-worker / fleans-custom-worker for worker
// grains), while dev runs (3-process) stay at Combined so fleans-core continues to
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
var fleansSilo = WithPersistence(apiProject, usePostgres, pg, sqliteConnectionString);
fleansSilo = WithStreaming(fleansSilo, useKafka, kafka);

// Web = Orleans client
WithPersistence(
    builder.AddProject<Projects.Fleans_Web>("fleans-management")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithEnvironment("Authentication__Authority", authAuthority)
        .WithEnvironment("Authentication__ClientId", authClientId)
        .WithEnvironment("Authentication__ClientSecret", authClientSecret)
        .WithReplicas(1),
    usePostgres, pg, sqliteConnectionString);

// MCP = Orleans client (for Claude Code)
WithPersistence(
    builder.AddProject<Projects.Fleans_Mcp>("fleans-mcp")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithHttpEndpoint(port: 5200, name: "mcp")
        .WithReplicas(1),
    usePostgres, pg, sqliteConnectionString);

// Publish-only topology: registers the dedicated Fleans.WorkerHost silo and the load-test
// nginx fan-out. Both are publish-time artifacts (aspire publish -t docker-compose / kubernetes
// or `dotnet run -- --publisher docker-compose ...`). Local dev (`dotnet run --project
// Fleans.Aspire`) keeps the original 3-process layout — Fleans.Api with the default Combined
// role still hosts worker grains there.
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
    workerHost = WithStreaming(workerHost, useKafka, kafka);

    // Custom worker host — worked example for the "host-your-own custom-task plugins"
    // pattern. Identical Orleans-cluster wiring to fleans-worker; differs in that this
    // host references ONLY Fleans.Worker + plugin assemblies (no Application/Domain refs).
    // Plugin authors copy this project as a starting template; the in-tree CustomWorkerHost
    // is what `aspire publish` emits as a separate fleans-custom-worker container so
    // operators can scale plugin workers independently from the engine workers.
    var customWorkerHost = builder.AddProject<Projects.Fleans_CustomWorkerHost>("fleans-custom-worker")
        .WithReference(orleans)
        .WaitFor(redis)
        .WithEnvironment("Fleans__Role", "Worker")
        .WithReplicas(1);
    customWorkerHost = WithStreaming(customWorkerHost, useKafka, kafka);

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

using var app = builder.Build();
await app.RunAsync();
