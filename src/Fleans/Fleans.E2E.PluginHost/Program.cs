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
// (WithReference(orleans) in Fleans.Aspire), or from FLEANS_LOAD_TEST_MODE outside Aspire.

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

    // Outside Aspire (tests/load docker-compose) there is no Orleans__* clustering config;
    // mirror Fleans.Api's FLEANS_LOAD_TEST_MODE wiring.
    if (builder.Configuration["FLEANS_LOAD_TEST_MODE"] == "true")
    {
        siloBuilder.UseRedisClustering(orleansRedis);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));
    }

    // Every silo owns a slice of the reminder-service hash ring, so engine reminders (BPMN
    // timers via TimerCallbackGrain) whose range lands here are served by THIS silo. Without a
    // reminder service the engine's RegisterOrUpdateReminder fails with a TypeLoadException
    // (Orleans.IReminderService) and the timer's activity fails; with an in-memory one those
    // timers lose durability (#788). Use the same Redis table as the engine (AddFleansReminders).
    siloBuilder.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));

    if (useKafka)
        siloBuilder.AddKafkaStreams(StreamProviderName, builder.Configuration.GetSection("Fleans:Streaming:Kafka"));
    else
        siloBuilder.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);
});

builder.Services.AddHttpClient(ProbeHandler.CallbackClientName);
builder.Services.AddProbePlugin();

builder.Build().Run();
