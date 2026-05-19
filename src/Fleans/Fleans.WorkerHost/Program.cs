using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.Plugins.RestCaller;
#if FLEANS_LOAD_TEST_MODE
using Fleans.Plugins.LoadDelay;
#endif
using Fleans.ServiceDefaults;
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
var siloName = $"{role}-{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();

var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");
var isLoadTestMode = builder.Configuration["FLEANS_LOAD_TEST_MODE"] == "true";

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

    if (isLoadTestMode && !string.IsNullOrEmpty(orleansRedisConnection))
    {
        siloBuilder.UseRedisClustering(orleansRedisConnection);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions =
                ConfigurationOptions.Parse(orleansRedisConnection));
        siloBuilder.UseInMemoryReminderService();
    }

    siloBuilder.AddFleanStreaming(builder.Configuration);
    siloBuilder.AddDashboard();
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();

    siloBuilder.AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>();
    siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddRestCallerPlugin();
#if FLEANS_LOAD_TEST_MODE
builder.Services.AddLoadDelayPlugin();
#endif
builder.AddFleansPersistence();

var app = builder.Build();
await app.EnsureDatabaseSchemaAsync();
app.MapDefaultEndpoints();
app.Run();
