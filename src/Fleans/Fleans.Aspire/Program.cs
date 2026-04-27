var builder = DistributedApplication.CreateBuilder(args);

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
var apiProject = builder.AddProject<Projects.Fleans_Api>("fleans-core")
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

// Load-testing topology: 2 silo replicas behind nginx, forced Postgres.
// Activated by: dotnet run --project Fleans.Aspire -- --publisher docker-compose --output-path <dir>
if (builder.ExecutionContext.IsPublishMode)
{
    apiProject = apiProject
        .WithHttpEndpoint(port: 8080, name: "http")
        .WithReplicas(2);

    builder.AddContainer("nginx", "nginx:1.27")
        .WithBindMount("../../tests/load/nginx.conf", "/etc/nginx/nginx.conf", isReadOnly: true)
        .WithHttpEndpoint(port: 80, targetPort: 80, name: "http")
        .WaitFor(apiProject);
}

using var app = builder.Build();
await app.RunAsync();
