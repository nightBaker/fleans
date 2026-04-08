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

if (usePostgres)
{
    // PostgreSQL provider: provision a containerised Postgres instance
    var pg = builder.AddPostgres("postgres")
        .WithDataVolume()
        .AddDatabase("fleans");

    // Api = Orleans silo (with PostgreSQL)
    var fleansSilo = builder.AddProject<Projects.Fleans_Api>("fleans-core")
        .WithReference(orleans)
        .WaitFor(redis)
        .WithReference(pg)
        .WaitFor(pg)
        .WithEnvironment("Persistence__Provider", "Postgres")
        .WithReplicas(1);

    // Web = Orleans client (with PostgreSQL)
    builder.AddProject<Projects.Fleans_Web>("fleans-management")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithReference(pg)
        .WaitFor(pg)
        .WithEnvironment("Persistence__Provider", "Postgres")
        .WithReplicas(1);

    // MCP = Orleans client (with PostgreSQL)
    builder.AddProject<Projects.Fleans_Mcp>("fleans-mcp")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithReference(pg)
        .WaitFor(pg)
        .WithEnvironment("Persistence__Provider", "Postgres")
        .WithHttpEndpoint(port: 5200, name: "mcp")
        .WithReplicas(1);
}
else
{
    // SQLite provider: shared database file for EF Core persistence (dev only)
    var sqliteDbPath = Path.Combine(Path.GetTempPath(), "fleans-dev.db");
    var sqliteConnectionString = $"DataSource={sqliteDbPath}";
    // Read replica connection — defaults to primary for dev (SQLite).
    // For production with PostgreSQL/SQL Server, point this at a read replica.
    var queryConnectionString = sqliteConnectionString;

    // Api = Orleans silo
    var fleansSilo = builder.AddProject<Projects.Fleans_Api>("fleans-core")
        .WithReference(orleans)
        .WaitFor(redis)
        .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
        .WithEnvironment("FLEANS_QUERY_CONNECTION", queryConnectionString)
        .WithReplicas(1);

    // Web = Orleans client
    builder.AddProject<Projects.Fleans_Web>("fleans-management")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
        .WithEnvironment("FLEANS_QUERY_CONNECTION", queryConnectionString)
        .WithReplicas(1);

    // MCP = Orleans client (for Claude Code)
    builder.AddProject<Projects.Fleans_Mcp>("fleans-mcp")
        .WithReference(orleans.AsClient())
        .WaitFor(fleansSilo)
        .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
        .WithEnvironment("FLEANS_QUERY_CONNECTION", queryConnectionString)
        .WithHttpEndpoint(port: 5200, name: "mcp")
        .WithReplicas(1);
}

using var app = builder.Build();
await app.RunAsync();
