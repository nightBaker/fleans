var builder = DistributedApplication.CreateBuilder(args);

// Shared SQLite database file for EF Core persistence (dev only)
var sqliteDbPath = Path.Combine(Path.GetTempPath(), "fleans-dev.db");
var sqliteConnectionString = $"DataSource={sqliteDbPath}";

// Add Redis for Orleans clustering and storage.
// Aspire 13.1+ auto-configures TLS for Redis containers, but the Orleans Redis
// provider doesn't negotiate TLS. Disable to avoid health check failures (dotnet/aspire#13612).
var redis = builder.AddRedis("orleans-redis").WithoutHttpsCertificate();

// Centralized Orleans configuration
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryStreaming("StreamProvider")
    .WithMemoryReminders();

// Api = Orleans silo
var fleansSilo = builder.AddProject<Projects.Fleans_Api>("fleans-core")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

// Web = Orleans client
builder.AddProject<Projects.Fleans_Web>("fleans-management")
    .WithReference(orleans.AsClient())
    .WaitFor(fleansSilo)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

using var app = builder.Build();
await app.RunAsync();
