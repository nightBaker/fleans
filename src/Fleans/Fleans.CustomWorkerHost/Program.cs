using Fleans.Plugins.RestCaller;
using Fleans.ServiceDefaults;
using Fleans.Worker.Placement;
using StackExchange.Redis;

// Fleans.CustomWorkerHost is a worked example for the "host-your-own custom-task plugins"
// pattern. It demonstrates the minimum-viable plugin worker silo: zero references to the
// engine projects (Application/Domain/Persistence/Infrastructure), only Fleans.Worker and
// the chosen plugin packages. Operators wanting to run their own plugin set in production
// can copy this Program.cs as a starting point, swap the plugin-registration lines for
// their own, and ship the resulting image alongside the engine.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

// Worker-only role — this host never claims engine grains.
if (string.IsNullOrEmpty(builder.Configuration["Fleans:Role"]))
{
    builder.Configuration["Fleans:Role"] = "Worker";
}

var roleRaw = builder.Configuration["Fleans:Role"]!;
var role = roleRaw.ToLowerInvariant();
if (role != "worker" && role != "combined")
{
    throw new InvalidOperationException(
        $"Fleans.CustomWorkerHost only supports Fleans:Role 'Worker' or 'Combined' (case-insensitive) — got '{roleRaw}'.");
}
var siloName = $"{role}-{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();

var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

    // Direct Redis clustering wiring (Aspire's WithReference flow does this for in-tree
    // silos; CustomWorkerHost demonstrates the manual path so plugin authors running
    // outside Aspire can copy it as a self-contained example).
    if (!string.IsNullOrEmpty(orleansRedisConnection))
    {
        siloBuilder.UseRedisClustering(orleansRedisConnection);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions =
                ConfigurationOptions.Parse(orleansRedisConnection));
        siloBuilder.UseInMemoryReminderService();
    }

    siloBuilder.AddFleanStreaming(builder.Configuration);

    // Worker placement strategy lives in Fleans.Worker — registered so plugin grains
    // carrying [WorkerPlacement] route to this silo.
    siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();
});

// Plugin registration — operator-controlled. Add or remove .Add*Plugin() calls here to
// pick which BPMN <serviceTask type="..."> values this host claims.
builder.Services.AddRestCallerPlugin();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
