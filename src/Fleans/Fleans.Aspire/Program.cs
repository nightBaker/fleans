var builder = DistributedApplication.CreateBuilder(args);

// Shared SQLite database file for EF Core persistence (dev only)
var sqliteDbPath = Path.Combine(Path.GetTempPath(), "fleans-dev.db");
var sqliteConnectionString = $"DataSource={sqliteDbPath}";

// Add Redis for Orleans clustering and storage
var redis = builder.AddRedis("redis");

// Centralized Orleans configuration
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryStreaming("StreamProvider")
    .WithMemoryReminders();

// Api = Orleans silo
builder.AddProject<Projects.Fleans_Api>("fleans")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

// Web = Orleans client
builder.AddProject<Projects.Fleans_Web>("fleans-client")
    .WithReference(orleans.AsClient())
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

using var app = builder.Build();
await app.RunAsync();
