var builder = DistributedApplication.CreateBuilder(args);

var orleans = builder.AddOrleans("default")
                     .WithDevelopmentClustering()
                     .WithMemoryGrainStorage("PubSubStore")
                     .WithMemoryStreaming("StreamProvider");

var orleansClient = builder.AddOrleans("default")
                     .WithDevelopmentClustering();

// Add our server project and reference your 'orleans' resource from it.
// it can join the Orleans cluster as a service.
// This implicitly add references to the required resources.
// In this case, that is the 'clusteringTable' resource declared earlier.

builder.AddProject<Projects.Fleans_Api>("fleans")
       .WithReference(orleans)
       .WithReplicas(1);

builder.AddProject<Projects.Fleans_Web>("fleans-client")
       .WithReference(orleansClient.AsClient())
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

