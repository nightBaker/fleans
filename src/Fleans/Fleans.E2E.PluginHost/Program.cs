using Fleans.E2E.PluginHost;
using Fleans.Streaming.Kafka;
using Fleans.Worker.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

// Test-only external plugin host for the E2E split-roles leg (#770). Shape mirrors the
// github.com/nightBaker/fleans-custom-worker-example template so the E2E suite exercises the
// same contract third-party plugin hosts rely on:
//   - AddFleansPluginHost validates Fleans:Role (Plugin/Combined only; Worker/Core throw)
//     and stamps the `plugin-<machine>-<guid>` silo name;
//   - the only plugin grain is ProbeHandler (`e2e-probe`), so Orleans' GetCompatibleSilos
//     can place it nowhere but here.
// Clustering + PubSubStore come from the Aspire-injected Orleans__* configuration
// (WithReference(orleans) in Fleans.Aspire). Outside Aspire, clustering follows
// Fleans:Clustering:Provider and PubSubStore comes from FLEANS_LOAD_TEST_MODE.

var builder = Host.CreateApplicationBuilder(args);

builder.AddKeyedRedisClient("orleans-redis");

// Stream provider must match the engine silos (Fleans:Streaming:Provider). Redis is the
// split-roles E2E default; Kafka is used by the tests/load multi-silo stack.
const string StreamProviderName = "StreamProvider";
var streamingProvider = builder.Configuration["Fleans:Streaming:Provider"] ?? "Redis";
var useKafka = streamingProvider.Equals("Kafka", StringComparison.OrdinalIgnoreCase);
if (!useKafka && !streamingProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"Fleans.E2E.PluginHost supports only the Redis and Kafka stream providers (got '{streamingProvider}').");
}

if (!useKafka)
{
    // The Redis stream provider resolves a non-keyed multiplexer; alias the Aspire keyed one.
    builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis"));

    // Must match the engine's FleanStreamingExtensions.AddRedisStreams — a different
    // TotalQueueCount would hash stream ids onto different queues than the engine silos.
    builder.Services.AddOptions<HashRingStreamQueueMapperOptions>(StreamProviderName)
        .Configure(o => o.TotalQueueCount = 8);
    builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);
    builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
        .Configure(o =>
        {
            o.MaxStreamLength = 1000;
            o.TrimTimeMinutes = 5;
        });
}

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddFleansPluginHost(builder.Configuration);

    var orleansRedis = builder.Configuration.GetConnectionString("orleans-redis")
        ?? throw new InvalidOperationException("ConnectionStrings:orleans-redis is required.");

    // Membership: under Aspire the Orleans:Clustering section wires it. Elsewhere (tests/load
    // docker-compose) use the engine's table, chosen by Fleans:Clustering:Provider (#885) — a plugin
    // silo on a different membership table would form a cluster of its own.
    if (!builder.Configuration.GetSection("Orleans:Clustering").Exists())
    {
        var clusteringProvider = builder.Configuration["Fleans:Clustering:Provider"] ?? "Redis";
        if (clusteringProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var clusterDb = builder.Configuration.GetConnectionString("fleans-cluster")
                ?? builder.Configuration.GetConnectionString("fleans")
                ?? throw new InvalidOperationException(
                    "Fleans:Clustering:Provider=Postgres needs ConnectionStrings:fleans-cluster or ConnectionStrings:fleans.");
            siloBuilder.UseAdoNetClustering(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = clusterDb;
            });
        }
        else if (clusteringProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
        {
            siloBuilder.UseRedisClustering(orleansRedis);
        }
        else
        {
            throw new InvalidOperationException(
                $"Fleans.E2E.PluginHost supports only the Redis and Postgres clustering providers (got '{clusteringProvider}').");
        }
    }

    // Outside Aspire there is no Orleans__* PubSubStore config; mirror Fleans.Api's
    // FLEANS_LOAD_TEST_MODE wiring.
    if (builder.Configuration["FLEANS_LOAD_TEST_MODE"] == "true")
    {
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));
    }

    // Every silo owns a slice of the reminder-service hash ring, so engine reminders (BPMN
    // timers via TimerCallbackGrain) whose range lands here are served by THIS silo. Without a
    // reminder service the engine's RegisterOrUpdateReminder fails with a TypeLoadException
    // (Orleans.IReminderService) and the timer's activity fails; with an in-memory one those
    // timers lose durability (#788). Use the same reminder table as the engine (AddFleansReminders):
    // Redis by default, or Postgres (own database first, then 'fleans') — #886.
    var remindersProvider = builder.Configuration["Fleans:Reminders:Provider"] ?? "Redis";
    if (remindersProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var remindersDb = builder.Configuration.GetConnectionString("fleans-reminders")
            ?? builder.Configuration.GetConnectionString("fleans")
            ?? throw new InvalidOperationException(
                "Fleans:Reminders:Provider=Postgres needs ConnectionStrings:fleans-reminders or ConnectionStrings:fleans.");
        siloBuilder.UseAdoNetReminderService(o =>
        {
            o.Invariant = "Npgsql";
            o.ConnectionString = remindersDb;
        });
    }
    else if (remindersProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
    {
        siloBuilder.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));
    }
    else
    {
        throw new InvalidOperationException(
            $"Fleans.E2E.PluginHost supports only the Redis and Postgres reminder providers (got '{remindersProvider}').");
    }
    siloBuilder.Configure<ReminderOptions>(builder.Configuration.GetSection("Fleans:Reminders"));

    if (useKafka)
        siloBuilder.AddKafkaStreams(StreamProviderName, builder.Configuration.GetSection("Fleans:Streaming:Kafka"));
    else
        siloBuilder.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);
});

builder.Services.AddHttpClient(ProbeHandler.CallbackClientName);
builder.Services.AddProbePlugin();

builder.Build().Run();
