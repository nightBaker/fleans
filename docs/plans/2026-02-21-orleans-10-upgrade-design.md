# Orleans 10 Upgrade + Dashboard + Aspire Configuration

**Date:** 2026-02-21

## Summary

Upgrade Orleans from 9.2.1 to 10.0.1, add the built-in Orleans Dashboard (co-hosted on the Web project at `/dashboard`), and centralize all Orleans infrastructure configuration in the Aspire AppHost using `AddOrleans`.

## Motivation

- Orleans 10.0 ships a built-in dashboard for real-time cluster monitoring (grain activations, method profiling, reminders, live logs).
- Aspire's `AddOrleans` resource model eliminates duplicated Redis configuration across Api and Web projects.
- Staying on the latest Orleans major version ensures access to performance improvements and security patches.

## Design

### 1. Package Version Bump (9.2.1 → 10.0.1)

All Orleans packages across the solution updated to 10.0.1:

| Project | Packages |
|---------|----------|
| Fleans.Domain | `Microsoft.Orleans.Sdk` |
| Fleans.Application | `Microsoft.Orleans.Sdk`, `Microsoft.Orleans.Reminders`, `Microsoft.Orleans.Streaming` |
| Fleans.Api | `Microsoft.Orleans.Server`, `Microsoft.Orleans.Clustering.Redis`, `Microsoft.Orleans.GrainDirectory.Redis`, `Microsoft.Orleans.Persistence.Redis` |
| Fleans.Web | `Microsoft.Orleans.Client`, `Microsoft.Orleans.Clustering.Redis`, `Microsoft.Orleans.GrainDirectory.Redis`, `Microsoft.Orleans.Persistence.Redis` |
| Fleans.Persistence | `Microsoft.Orleans.Sdk` |
| Fleans.ServiceDefaults | `Microsoft.Orleans.Sdk` |
| Fleans.Aspire | `Microsoft.Orleans.Clustering.Redis` |
| Fleans.Application.Tests | `Microsoft.Orleans.TestingHost`, `Microsoft.Orleans.Serialization.NewtonsoftJson` |

New packages to add:
- `Microsoft.Orleans.Dashboard` → Fleans.Api (silo data collection) and Fleans.Web (client UI)
- `Microsoft.Orleans.Dashboard.Abstractions` → Fleans.Web
- `Aspire.StackExchange.Redis` → Fleans.Api and Fleans.Web (provides `AddKeyedRedisClient` for Aspire-managed Orleans)

### 2. Aspire AppHost — Centralized Orleans Configuration

**Fleans.Aspire/Program.cs** becomes the single source of truth for Orleans infrastructure:

```csharp
var redis = builder.AddRedis("redis");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryStreaming("StreamProvider")
    .WithMemoryReminders();

builder.AddProject<Projects.Fleans_Api>("fleans")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

builder.AddProject<Projects.Fleans_Web>("fleans-client")
    .WithReference(orleans.AsClient())
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);
```

### 3. Api Program.cs — Simplified

Remove all manual Redis `ConfigurationOptions.Parse` and clustering config. Keep only app-specific configuration:

```csharp
builder.AddKeyedRedisClient("redis");

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddDashboard();
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();
});
```

### 4. Web Program.cs — Simplified + Dashboard

Replace `builder.Host.UseOrleansClient(siloBuilder => siloBuilder.UseRedisClustering(...))` with:

```csharp
builder.AddKeyedRedisClient("redis");

builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.AddDashboard();
});

// After app.Build():
app.MapOrleansDashboard(routePrefix: "/dashboard");
```

### 5. Test Project — No Aspire Changes

`Fleans.Application.Tests` uses `TestClusterBuilder` with in-memory clustering. Only the Orleans package versions change; no Aspire configuration is needed for tests.

## Breaking Change Audit

| Change | Impact on Fleans | Action |
|--------|-----------------|--------|
| `AddGrainCallFilter` removed | Not affected — already uses `siloBuilder.AddIncomingGrainCallFilter` | None |
| `RegisterTimer` obsoleted | Not affected — Fleans uses custom `RegisterTimerReminder` domain method, not Orleans `Grain.RegisterTimer` | None |
| `OrleansConstructorAttribute` obsoleted | Not used in Fleans | None |
| ADO.NET provider changes | Not used (SQLite via EF Core) | None |
| `CancelRequestOnTimeout` default changed | Low risk — review if any grain calls depend on cancellation-on-timeout behavior | Verify in testing |

## Files Changed

- `Fleans.Aspire/Program.cs` — centralized Orleans resource model
- `Fleans.Aspire/Fleans.Aspire.csproj` — package updates
- `Fleans.Api/Program.cs` — remove manual Redis config, add dashboard
- `Fleans.Api/Fleans.Api.csproj` — package updates, add Dashboard + Aspire.StackExchange.Redis
- `Fleans.Web/Program.cs` — replace manual client config, add dashboard + MapOrleansDashboard
- `Fleans.Web/Fleans.Web.csproj` — package updates, add Dashboard + Aspire.StackExchange.Redis
- All other .csproj files — Orleans version bump 9.2.1 → 10.0.1
