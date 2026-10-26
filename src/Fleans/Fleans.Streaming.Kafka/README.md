# Fleans.Streaming.Kafka

The Kafka stream provider the Fleans engine silos use when `Fleans:Streaming:Provider=Kafka`.
A plugin host (a `Fleans.Worker` silo that joins the engine's cluster) must stream over the same
provider as the engine, so a host on a Kafka-streamed cluster registers it with the engine's
settings:

```csharp
using Fleans.Streaming.Kafka;

builder.UseOrleans(silo =>
{
    silo.AddFleansPluginHost(builder.Configuration);
    silo.AddKafkaStreams("StreamProvider", builder.Configuration.GetSection("Fleans:Streaming:Kafka"));
    // ... clustering and reminders on the engine's providers
});
```

The provider name must be `StreamProvider`, and every silo of the cluster subscribes through
implicit subscriptions only — `AddKafkaStreams` configures that. Options (`Brokers`, `QueueCount`,
`TopicPrefix`, dead-letter queue, …) are documented in the
[Streaming reference](https://nightbaker.github.io/fleans/reference/streaming/).
