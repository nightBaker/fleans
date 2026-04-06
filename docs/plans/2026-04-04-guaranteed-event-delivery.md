# Guaranteed Event Delivery Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the Orleans stream provider pluggable via configuration and harden event handlers for persistent stream retry semantics.

**Architecture:** Extract hardcoded `WithMemoryStreaming` into a config-driven extension method on `ISiloBuilder`. Move streaming setup from Aspire AppHost into the Api silo's `UseOrleans` callback so the silo owns its stream provider choice. Harden handlers with double-fault protection.

**Tech Stack:** Orleans 10.x streaming, .NET 10, Aspire

---

### Task 1: Create `AddFleanStreaming` Extension Method

**Files:**
- Create: `src/Fleans/Fleans.ServiceDefaults/FleanStreamingExtensions.cs`

**Step 1: Write the extension method**

```csharp
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;

namespace Microsoft.Extensions.Hosting;

public static class FleanStreamingExtensions
{
    public const string StreamProviderName = "StreamProvider";

    public static ISiloBuilder AddFleanStreaming(this ISiloBuilder builder, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Fleans:Streaming:Provider") ?? "memory";

        return provider switch
        {
            "memory" => builder.AddMemoryStreams(StreamProviderName),
            _ => throw new ArgumentException(
                $"Unknown streaming provider '{provider}'. Supported: memory. " +
                $"To add a provider, install its NuGet package and add a case to {nameof(FleanStreamingExtensions)}.{nameof(AddFleanStreaming)}.")
        };
    }
}
```

**Step 2: Build to verify it compiles**

Run: `dotnet build src/Fleans/Fleans.ServiceDefaults/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.ServiceDefaults/FleanStreamingExtensions.cs
git commit -m "feat: add pluggable stream provider configuration extension (#160)"
```

---

### Task 2: Wire Up Aspire and Api to Use Config-Driven Streaming

**Files:**
- Modify: `src/Fleans/Fleans.Aspire/Program.cs:13-17` — remove `WithMemoryStreaming`
- Modify: `src/Fleans/Fleans.Api/Program.cs:21-31` — add `AddFleanStreaming` call

**Step 1: Update Aspire Program.cs — remove `WithMemoryStreaming`**

Replace lines 13-17:
```csharp
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryStreaming("StreamProvider")
    .WithMemoryReminders();
```

With:
```csharp
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryReminders();
```

**Step 2: Update Api Program.cs — add streaming in UseOrleans callback**

Replace lines 21-31:
```csharp
builder.UseOrleans(siloBuilder =>
{
    // Dashboard data collection (UI served from Web project)
    siloBuilder.AddDashboard();

    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();

    // JournaledGrain event sourcing: use CustomStorage backed by EfCoreEventStore
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();
});
```

With:
```csharp
builder.UseOrleans(siloBuilder =>
{
    // Pluggable stream provider — reads Fleans:Streaming:Provider from config (default: memory)
    siloBuilder.AddFleanStreaming(builder.Configuration);

    // Dashboard data collection (UI served from Web project)
    siloBuilder.AddDashboard();

    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();

    // JournaledGrain event sourcing: use CustomStorage backed by EfCoreEventStore
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();
});
```

**Step 3: Build the full solution**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded, 0 errors

**Step 4: Run existing tests to verify nothing broke**

Run: `dotnet test src/Fleans/`
Expected: All tests pass — streaming still works with default "memory" provider

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Aspire/Program.cs src/Fleans/Fleans.Api/Program.cs
git commit -m "feat: move streaming config from Aspire to silo with pluggable provider (#160)"
```

---

### Task 3: Add Double-Fault Protection to Event Handlers

Both handlers already have try/catch that calls `FailActivity` on error. But if `FailActivity` itself throws (e.g., grain deactivated, network issue), the exception propagates to the stream and may cause infinite retry. Add a guard.

**Files:**
- Modify: `src/Fleans/Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs:36-41`
- Modify: `src/Fleans/Fleans.Application/Events/Handlers/WorfklowEvaluateConditionEventHandler.cs:31-35`

**Step 1: Update WorkflowExecuteScriptEventHandler catch block**

Replace the catch block (lines 39-41):
```csharp
catch (Exception ex)
{
    LogScriptExecutionFailed(ex, item.ActivityId);
    await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
}
```

With:
```csharp
catch (Exception ex)
{
    LogScriptExecutionFailed(ex, item.ActivityId);
    try
    {
        await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
    }
    catch (Exception failEx)
    {
        LogFailActivityFailed(failEx, item.ActivityId);
    }
}
```

**Step 2: Add LoggerMessage for the new log**

Add after `LogStreamError` (after line 51):
```csharp
[LoggerMessage(EventId = 4014, Level = LogLevel.Critical, Message = "FailActivity call itself failed for activity {ActivityId} — workflow may be stalled")]
private partial void LogFailActivityFailed(Exception ex, string activityId);
```

**Step 3: Update WorfklowEvaluateConditionEventHandler catch block**

Replace the catch block (lines 34-35):
```csharp
catch (Exception ex)
{
    LogConditionEvaluationFailed(ex, item.ActivityId);
    await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
}
```

With:
```csharp
catch (Exception ex)
{
    LogConditionEvaluationFailed(ex, item.ActivityId);
    try
    {
        await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
    }
    catch (Exception failEx)
    {
        LogFailActivityFailed(failEx, item.ActivityId);
    }
}
```

**Step 4: Add LoggerMessage for the new log**

Add after `LogStreamError` (after line 42):
```csharp
[LoggerMessage(EventId = 4005, Level = LogLevel.Critical, Message = "FailActivity call itself failed for activity {ActivityId} — workflow may be stalled")]
private partial void LogFailActivityFailed(Exception ex, string activityId);
```

**Step 5: Build**

Run: `dotnet build src/Fleans/Fleans.Application/`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/Events/Handlers/
git commit -m "feat: add double-fault protection to event handlers (#160)"
```

---

### Task 4: Update `WorkflowEventsPublisher` to Use Shared Constant

**Files:**
- Modify: `src/Fleans/Fleans.Application/Events/WorkflowEventsPublisher.cs:16`

**Step 1: Replace hardcoded string with shared constant**

Replace line 16:
```csharp
public const string StreamProvider = "StreamProvider";
```

With:
```csharp
public const string StreamProvider = FleanStreamingExtensions.StreamProviderName;
```

This ensures the publisher and configuration always agree on the provider name.

**Step 2: Build**

Run: `dotnet build src/Fleans/Fleans.Application/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/Events/WorkflowEventsPublisher.cs
git commit -m "refactor: use shared stream provider name constant (#160)"
```

---

### Task 5: Run Full Test Suite and Verify

**Step 1: Build the full solution**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded, 0 errors

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 3: Final commit if any fixups needed**

---

### Task 6: Update Design Doc with Implementation Notes

**Files:**
- Modify: `docs/plans/2026-04-04-guaranteed-event-delivery-design.md`

**Step 1: Add "How to Add a New Stream Provider" section**

Append to the design doc:

```markdown
## How to Add a New Stream Provider

1. Install the NuGet package for the Orleans stream adapter (e.g., `Microsoft.Orleans.Streaming.EventHubs`)
2. Add a case to `FleanStreamingExtensions.AddFleanStreaming()` in `Fleans.ServiceDefaults/FleanStreamingExtensions.cs`:
   ```csharp
   "eventhubs" => builder.AddEventHubStreams(StreamProviderName, options =>
   {
       options.ConfigureEventHub(hub => hub.Configure(cfg =>
       {
           cfg.ConnectionString = configuration["Fleans:Streaming:EventHubs:ConnectionString"];
           cfg.ConsumerGroup = configuration["Fleans:Streaming:EventHubs:ConsumerGroup"] ?? "$Default";
           cfg.Path = configuration["Fleans:Streaming:EventHubs:Path"] ?? "fleans-events";
       }));
       options.UseAzureTableCheckpointer(table => table.Configure(cfg =>
       {
           cfg.TableServiceClient = new Azure.Data.Tables.TableServiceClient(
               configuration["Fleans:Streaming:EventHubs:StorageConnectionString"]);
       }));
   }),
   ```
3. Set configuration:
   ```json
   {
     "Fleans": {
       "Streaming": {
         "Provider": "eventhubs",
         "EventHubs": {
           "ConnectionString": "...",
           "Path": "fleans-events"
         }
       }
     }
   }
   ```
4. No changes to publishers or handlers — they use `IAsyncStream<T>` which is provider-agnostic.
```

**Step 2: Commit**

```bash
git add docs/plans/2026-04-04-guaranteed-event-delivery-design.md
git commit -m "docs: add stream provider extension guide (#160)"
```
