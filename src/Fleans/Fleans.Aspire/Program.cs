var builder = DistributedApplication.CreateBuilder(args);

// Persistence provider — set FLEANS_PERSISTENCE_PROVIDER=Postgres to use PostgreSQL.
// Default is SQLite (fast local dev, no container required).
var persistenceProvider = builder.Configuration["FLEANS_PERSISTENCE_PROVIDER"] ?? "Sqlite";
var usePostgres = persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);

// Add Redis for Orleans clustering and storage.
// Aspire 13.1+ auto-configures TLS for Redis containers, but the Orleans Redis
// provider doesn't negotiate TLS. Disable to avoid health check failures (dotnet/aspire#13612).
var redis = builder.AddRedis("orleans-redis").WithoutHttpsCertificate();

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

// Api = Orleans silo
var apiProject = builder.AddProject<Projects.Fleans_Api>("fleans-core")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithReplicas(1);
if (usePostgres) apiProject = apiProject.WaitFor(pg!);
var fleansSilo = WithPersistence(apiProject, usePostgres, pg, sqliteConnectionString);

// Web = Orleans client
WithPersistence(
    builder.AddProject<Projects.Fleans_Web>("fleans-management")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
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

using var app = builder.Build();
await app.RunAsync();
