var builder = DistributedApplication.CreateBuilder(args);

// Shared SQLite database file for EF Core persistence (dev only)
var sqliteDbPath = Path.Combine(Path.GetTempPath(), "fleans-dev.db");
var sqliteConnectionString = $"DataSource={sqliteDbPath}";

// Add Redis for Orleans clustering and storage
var redis = builder.AddRedis("redis");

// Configure Orleans with Redis clustering
// var orleans = builder.AddOrleans("default")
//                      .WithRedisGrainStorage("PubSubStore", redis)
//                      .WithRedisStreaming("StreamProvider", redis);

// Add our server project and reference your 'orleans' resource from it.
// it can join the Orleans cluster as a service.
// This implicitly add references to the required resources.
// In this case, that is the 'clusteringTable' resource declared earlier.

builder.AddProject<Projects.Fleans_Api>("fleans")
       .WithReference(redis)
       .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
       .WithReplicas(1);

builder.AddProject<Projects.Fleans_Web>("fleans-client")
       .WithReference(redis)
       .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
       .WithReplicas(1);

// Reference the Orleans resource as a client from the 'frontend'
// project so that it can connect to the Orleans cluster.
//builder.AddProject<Projects.OrleansClient>("frontend")
//       .WithReference(orleans.AsClient())
//       .WithExternalHttpEndpoints()
//       .WithReplicas(3);

// Build and run the application.
using var app = builder.Build();
await app.RunAsync();

