# Guaranteed Event Delivery — Design

**Issue:** [#160](https://github.com/nightBaker/fleans/issues/160)
**Date:** 2026-04-04

## Problem

`WorkflowEventsPublisher` publishes domain events (`EvaluateConditionEvent`, `ExecuteScriptEvent`) to Orleans in-memory streams. If a handler crashes or the silo restarts mid-processing, these events are lost and the workflow stalls. No retry, no dead-letter queue, no replay.

Note: aggregate state events are already durable via JournaledGrain + EfCoreEventStore. Only the two external processing events are at risk.

## Approach: Pluggable Persistent Stream Providers

Replace the hardcoded `WithMemoryStreaming("StreamProvider")` with a configuration-driven stream provider selection. Use existing Orleans stream adapter packages (EventHubs, SQS, community RabbitMQ/Kafka) — no custom adapters.

## Design

### 1. Stream Provider Pluggability

A config key selects the provider:

```json
{
  "Fleans": {
    "Streaming": {
      "Provider": "memory"
    }
  }
}
```

Extension method in `Fleans.ServiceDefaults`:

```csharp
public static ISiloBuilder AddFleanStreaming(this ISiloBuilder builder, IConfiguration config)
{
    var provider = config.GetValue<string>("Fleans:Streaming:Provider") ?? "memory";
    return provider switch
    {
        "memory" => builder.AddMemoryStreams("StreamProvider"),
        // Future: "eventhubs", "sqs", "rabbitmq", "kafka"
        _ => throw new ArgumentException($"Unknown stream provider: {provider}")
    };
}
```

Adding a new provider:
1. Add the NuGet package (e.g., `Microsoft.Orleans.Streaming.EventHubs`)
2. Add a case to the switch with provider-specific config
3. No changes to publishers or handlers

The stream provider name `"StreamProvider"` stays constant — publishers and handlers are unaware of the backing store.

### 2. Handler Idempotency & Error Handling

Persistent streams can redeliver events. Handlers must be safe on retry.

**Natural idempotency already exists** — `WorkflowExecution.MarkCompleted()` and `CompleteConditionSequence()` check if the activity is still active. Duplicate calls are rejected by the domain model.

**Additions:**
- **Try/catch in handlers** — catch exceptions, call `FailActivity` so the workflow transitions to error state instead of retrying forever
- **Poison message protection** — unrecoverable errors (bad script, invalid expression) fail the activity immediately rather than blocking the stream
- **No external dedup store needed** — domain model state guards provide idempotency

### 3. What Changes

| Component | Changes? | Details |
|-----------|----------|---------|
| `WorkflowEventsPublisher` | No | Already uses `IAsyncStream<T>` — provider-agnostic |
| `WorkflowExecuteScriptEventHandler` | Minor | Add try/catch, call `FailActivity` on unrecoverable errors |
| `WorfklowEvaluateConditionEventHandler` | Minor | Same error handling |
| `Fleans.ServiceDefaults` | Yes | New `AddFleanStreaming()` extension method |
| `Fleans.Aspire/Program.cs` | Yes | Remove `WithMemoryStreaming()`, use config-driven setup |
| `Fleans.Api/Program.cs` | Yes | Call `AddFleanStreaming()` in `UseOrleans` callback |
| `Fleans.Web/Program.cs` | Yes | Call streaming config extension |
| `appsettings.json` | Yes | New `Fleans:Streaming:Provider` config section |
| Domain / Aggregate | No | No changes |
| Persistence / EfCoreEventStore | No | No changes |

## Non-Goals

- Building custom stream adapters (use existing packages)
- Changing the domain event model or aggregate event sourcing
- Adding new database tables
