# Fleans.Streaming.Kafka

The Kafka stream provider the Fleans engine silos use when `Fleans:Streaming:Provider=Kafka`.
Custom-task workers connect to the API over gRPC and never need it.

```csharp
using Fleans.Streaming.Kafka;

builder.UseOrleans(silo =>
{
    silo.AddKafkaStreams("StreamProvider", builder.Configuration.GetSection("Fleans:Streaming:Kafka"));
});
```

The provider name must be `StreamProvider`, and every silo of the cluster subscribes through
implicit subscriptions only — `AddKafkaStreams` configures that. Options (`Brokers`, `QueueCount`,
`TopicPrefix`, dead-letter queue, …) are documented in the
[Streaming reference](https://nightbaker.github.io/fleans/reference/streaming/).
