using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.Plugins.RestCaller;
using Fleans.ServiceDefaults;
using Fleans.ServiceDefaults.Clustering;
using Fleans.ServiceDefaults.Reminders;
using Fleans.Worker.Hosting;
using Fleans.Worker.Placement;
using Orleans.Dashboard;
using Orleans.EventSourcing.CustomStorage;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

// This is the dedicated worker silo image (fleans-worker). Default Fleans:Role to Worker
// so the deployable runs as a worker silo out of the box; operators can still override
// via configuration to run a Combined silo if needed.
if (string.IsNullOrEmpty(builder.Configuration["Fleans:Role"]))
{
    builder.Configuration["Fleans:Role"] = "Worker";
}

var roleRaw = builder.Configuration["Fleans:Role"]!;
var role = roleRaw.ToLowerInvariant();
if (role == "plugin")
{
    throw new InvalidOperationException(
        "Fleans.WorkerHost does not support Fleans:Role=Plugin. The 'Plugin' role is reserved " +
        "for external custom worker hosts (see docs/concepts/custom-tasks.md). Use Core, " +
        "Worker, or Combined for engine silos.");
}
if (role != "core" && role != "worker" && role != "combined")
{
    throw new InvalidOperationException(
        $"Fleans:Role must be one of 'Core', 'Worker', 'Combined' (case-insensitive) — got '{roleRaw}'.");
}
// With Fleans:Kubernetes:Enabled the silo name is the pod name (which must then start with the role).
var siloName = FleansClusteringExtensions.ResolveSiloName(builder.Configuration, role);

var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");
var isLoadTestMode = builder.Configuration["FLEANS_LOAD_TEST_MODE"] == "true";

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

    // Membership table (Fleans:Clustering:Provider, #885) + opt-in Kubernetes hosting.
    siloBuilder.AddFleansClustering(builder.Configuration);

    if (isLoadTestMode && !string.IsNullOrEmpty(orleansRedisConnection))
    {
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions =
                ConfigurationOptions.Parse(orleansRedisConnection));
    }

    // Fleans reminders: Redis-backed for BPMN timer durability across silo restarts (#650).
    // Fails fast at startup if 'orleans-redis' connection string is missing.
    siloBuilder.AddFleansReminders(builder.Configuration);

    siloBuilder.AddFleanStreaming(builder.Configuration);
    siloBuilder.AddDashboard();
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();

    siloBuilder.AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>();
    siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();

    // Fail fast on Fleans:Role / placement-attribute mismatch (#457).
    siloBuilder.AddFleansPlacementAssertion(builder.Configuration);
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddRestCallerPlugin();
builder.AddFleansPersistence();

var app = builder.Build();
await app.EnsureDatabaseSchemaAsync();
app.MapDefaultEndpoints();
app.Run();
