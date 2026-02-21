# Orleans 10 Upgrade + Dashboard + Aspire Config Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Upgrade Orleans from 9.2.1 to 10.0.1, add the built-in Orleans Dashboard at `/dashboard` on the Web project, and centralize all Orleans config in the Aspire AppHost.

**Architecture:** Three-phase approach: (1) bump all Orleans package versions, (2) centralize Orleans config in Aspire and simplify Api/Web Program.cs, (3) add the Dashboard. Each phase builds + tests before moving on.

**Tech Stack:** Orleans 10.0.1, Aspire.Hosting.Orleans, Microsoft.Orleans.Dashboard, Aspire.StackExchange.Redis, Redis, .NET 10

**Worktree:** `/Users/yerassylshalabayev/RiderProjects/fleans/.worktrees/feature/orleans-10-upgrade`
**Solution root:** `src/Fleans/` (relative to worktree)

---

### Task 1: Bump Orleans packages from 9.2.1 to 10.0.1

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Fleans.Domain.csproj:17` — `Microsoft.Orleans.Sdk`
- Modify: `src/Fleans/Fleans.Application/Fleans.Application.csproj:11-13` — `Microsoft.Orleans.Reminders`, `Microsoft.Orleans.Sdk`, `Microsoft.Orleans.Streaming`
- Modify: `src/Fleans/Fleans.Api/Fleans.Api.csproj:14-17` — `Microsoft.Orleans.Clustering.Redis`, `Microsoft.Orleans.GrainDirectory.Redis`, `Microsoft.Orleans.Persistence.Redis`, `Microsoft.Orleans.Server`
- Modify: `src/Fleans/Fleans.Web/Fleans.Web.csproj:22-25` — `Microsoft.Orleans.Client`, `Microsoft.Orleans.Clustering.Redis`, `Microsoft.Orleans.GrainDirectory.Redis`, `Microsoft.Orleans.Persistence.Redis`
- Modify: `src/Fleans/Fleans.Persistence/Fleans.Persistence.csproj:12` — `Microsoft.Orleans.Sdk`
- Modify: `src/Fleans/Fleans.ServiceDefaults/Fleans.ServiceDefaults.csproj:16` — `Microsoft.Orleans.Sdk`
- Modify: `src/Fleans/Fleans.Aspire/Fleans.Aspire.csproj:17` — `Microsoft.Orleans.Clustering.Redis`
- Modify: `src/Fleans/Fleans.Application.Tests/Fleans.Application.Tests.csproj:19-20` — `Microsoft.Orleans.Serialization.NewtonsoftJson`, `Microsoft.Orleans.TestingHost`

**Step 1: Update all .csproj files**

In every file listed above, replace `Version="9.2.1"` with `Version="10.0.1"` for all `Microsoft.Orleans.*` packages. Here's the full list of edits:

`Fleans.Domain.csproj` line 17:
```xml
<PackageReference Include="Microsoft.Orleans.Sdk" Version="10.0.1" />
```

`Fleans.Application.csproj` lines 11-13:
```xml
<PackageReference Include="Microsoft.Orleans.Reminders" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Sdk" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Streaming" Version="10.0.1" />
```

`Fleans.Api.csproj` lines 14-17:
```xml
<PackageReference Include="Microsoft.Orleans.Clustering.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.GrainDirectory.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Persistence.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Server" Version="10.0.1" />
```

`Fleans.Web.csproj` lines 22-25:
```xml
<PackageReference Include="Microsoft.Orleans.Client" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Clustering.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.GrainDirectory.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Persistence.Redis" Version="10.0.1" />
```

`Fleans.Persistence.csproj` line 12:
```xml
<PackageReference Include="Microsoft.Orleans.Sdk" Version="10.0.1" />
```

`Fleans.ServiceDefaults.csproj` line 16:
```xml
<PackageReference Include="Microsoft.Orleans.Sdk" Version="10.0.1" />
```

`Fleans.Aspire.csproj` line 17:
```xml
<PackageReference Include="Microsoft.Orleans.Clustering.Redis" Version="10.0.1" />
```

`Fleans.Application.Tests.csproj` lines 19-20:
```xml
<PackageReference Include="Microsoft.Orleans.Serialization.NewtonsoftJson" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.TestingHost" Version="10.0.1" />
```

**Step 2: Restore and build**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds with 0 errors. Watch for any new warnings about obsolete APIs.

**Step 3: Run all tests**

Run: `dotnet test src/Fleans/Fleans.sln`
Expected: All 290 tests pass.

**Step 4: Commit**

```bash
git add -A
git commit -m "chore: upgrade Orleans packages from 9.2.1 to 10.0.1"
```

---

### Task 2: Add Aspire.StackExchange.Redis to Api and Web

The Aspire Orleans integration requires `AddKeyedRedisClient("redis")` in service projects so Orleans can resolve Redis connections via keyed DI. This extension comes from the `Aspire.StackExchange.Redis` package.

**Files:**
- Modify: `src/Fleans/Fleans.Api/Fleans.Api.csproj` — add `Aspire.StackExchange.Redis` package
- Modify: `src/Fleans/Fleans.Web/Fleans.Web.csproj` — add `Aspire.StackExchange.Redis` package

**Step 1: Add packages**

In `Fleans.Api.csproj`, add to the `<ItemGroup>` containing packages (after line 20):
```xml
<PackageReference Include="Aspire.StackExchange.Redis" Version="9.2.0" />
```

In `Fleans.Web.csproj`, add to the `<ItemGroup>` containing packages (after line 25):
```xml
<PackageReference Include="Aspire.StackExchange.Redis" Version="9.2.0" />
```

> Note: Use version 9.2.0 which is the latest Aspire component version compatible with the 9.0.0 Aspire SDK. Verify the exact latest version on NuGet during implementation — search for `Aspire.StackExchange.Redis`.

**Step 2: Build**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add -A
git commit -m "chore: add Aspire.StackExchange.Redis to Api and Web"
```

---

### Task 3: Centralize Orleans config in Aspire AppHost

Move all Orleans infrastructure config (clustering, grain storage, streaming, reminders) from Api/Web Program.cs into the Aspire AppHost.

**Files:**
- Modify: `src/Fleans/Fleans.Aspire/Program.cs` — replace manual project references with `AddOrleans` resource model
- Modify: `src/Fleans/Fleans.Api/Program.cs` — remove manual Redis/clustering config, add `AddKeyedRedisClient`, simplify `UseOrleans`
- Modify: `src/Fleans/Fleans.Web/Program.cs` — remove manual Redis/clustering config, add `AddKeyedRedisClient`, use parameterless `UseOrleansClient`

**Step 1: Rewrite Aspire Program.cs**

Replace the full content of `src/Fleans/Fleans.Aspire/Program.cs` with:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Shared SQLite database file for EF Core persistence (dev only)
var sqliteDbPath = Path.Combine(Path.GetTempPath(), "fleans-dev.db");
var sqliteConnectionString = $"DataSource={sqliteDbPath}";

// Add Redis for Orleans clustering and storage
var redis = builder.AddRedis("redis");

// Centralized Orleans configuration
var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis)
    .WithGrainStorage("PubSubStore", redis)
    .WithMemoryStreaming("StreamProvider")
    .WithMemoryReminders();

// Api = Orleans silo
builder.AddProject<Projects.Fleans_Api>("fleans")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

// Web = Orleans client
builder.AddProject<Projects.Fleans_Web>("fleans-client")
    .WithReference(orleans.AsClient())
    .WaitFor(redis)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);

using var app = builder.Build();
await app.RunAsync();
```

**Step 2: Simplify Api Program.cs**

Replace the Orleans silo configuration block (lines 14-68) with:

```csharp
// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("redis");

// Orleans silo configuration
// Infrastructure (clustering, storage, streaming, reminders) is managed by Aspire AppHost
builder.UseOrleans(siloBuilder =>
{
    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();
});
```

Also remove the `using StackExchange.Redis;` import from the top of the file (line 6) since `ConfigurationOptions` is no longer used.

**Step 3: Simplify Web Program.cs**

Replace the Orleans client configuration block (lines 31-52) with:

```csharp
// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("redis");

// Orleans client — Aspire injects clustering config automatically
builder.UseOrleansClient();
```

Also remove the `using StackExchange.Redis;` import from the top of the file (line 8).

**Step 4: Build**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds. The `StackExchange.Redis` using removal may produce a warning if there are other usages — check carefully.

**Step 5: Run all tests**

Run: `dotnet test src/Fleans/Fleans.sln`
Expected: All 290 tests pass. Tests use `TestClusterBuilder` which doesn't go through Aspire, so they should be unaffected.

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor: centralize Orleans config in Aspire AppHost

Move Redis clustering, grain storage, streaming, and reminders
configuration from Api/Web Program.cs into the Aspire AppHost
using AddOrleans resource model."
```

---

### Task 4: Add Orleans Dashboard to Api (silo) and Web (client)

**Files:**
- Modify: `src/Fleans/Fleans.Api/Fleans.Api.csproj` — add `Microsoft.Orleans.Dashboard` package
- Modify: `src/Fleans/Fleans.Web/Fleans.Web.csproj` — add `Microsoft.Orleans.Dashboard` package
- Modify: `src/Fleans/Fleans.Api/Program.cs` — add `siloBuilder.AddDashboard()`
- Modify: `src/Fleans/Fleans.Web/Program.cs` — add `clientBuilder.AddDashboard()` and `app.MapOrleansDashboard()`

**Step 1: Add Dashboard NuGet packages**

In `Fleans.Api.csproj`, add to the packages `<ItemGroup>`:
```xml
<PackageReference Include="Microsoft.Orleans.Dashboard" Version="10.0.1" />
```

In `Fleans.Web.csproj`, add to the packages `<ItemGroup>`:
```xml
<PackageReference Include="Microsoft.Orleans.Dashboard" Version="10.0.1" />
```

**Step 2: Add Dashboard to Api silo**

In `src/Fleans/Fleans.Api/Program.cs`, inside the `builder.UseOrleans(siloBuilder => { ... })` block, add:

```csharp
// Dashboard data collection (UI served from Web project)
siloBuilder.AddDashboard();
```

The full UseOrleans block should now look like:

```csharp
builder.UseOrleans(siloBuilder =>
{
    // Dashboard data collection (UI served from Web project)
    siloBuilder.AddDashboard();

    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();
});
```

**Step 3: Add Dashboard to Web client**

In `src/Fleans/Fleans.Web/Program.cs`, replace the parameterless `builder.UseOrleansClient();` with:

```csharp
// Orleans client with Dashboard UI
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.AddDashboard();
});
```

Then, after `app.MapStaticAssets();` (around line 77) and before `app.Run();`, add:

```csharp
// Orleans Dashboard at /dashboard
app.MapOrleansDashboard(routePrefix: "/dashboard");
```

**Step 4: Build**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds.

**Step 5: Run all tests**

Run: `dotnet test src/Fleans/Fleans.sln`
Expected: All 290 tests pass.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: add Orleans Dashboard at /dashboard on Web project

Dashboard data collection runs on the Api silo.
Dashboard UI is served from the Web (Blazor) project at /dashboard."
```

---

### Task 5: Remove redundant Redis packages from Web and Aspire

Now that Aspire manages Orleans infrastructure, the Web project no longer needs direct Redis clustering/persistence packages (those are only needed on the silo side, and Aspire handles injection). The Aspire project also no longer needs the Orleans.Clustering.Redis package directly.

**Files:**
- Modify: `src/Fleans/Fleans.Web/Fleans.Web.csproj` — remove `Microsoft.Orleans.Clustering.Redis`, `Microsoft.Orleans.GrainDirectory.Redis`, `Microsoft.Orleans.Persistence.Redis`
- Modify: `src/Fleans/Fleans.Aspire/Fleans.Aspire.csproj` — remove `Microsoft.Orleans.Clustering.Redis`

**Step 1: Clean up Web csproj**

Remove these three lines from `Fleans.Web.csproj`:
```xml
<PackageReference Include="Microsoft.Orleans.Clustering.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.GrainDirectory.Redis" Version="10.0.1" />
<PackageReference Include="Microsoft.Orleans.Persistence.Redis" Version="10.0.1" />
```

The Web project should still have `Microsoft.Orleans.Client` (for grain interfaces) and `Aspire.StackExchange.Redis` (for `AddKeyedRedisClient`).

**Step 2: Clean up Aspire csproj**

Remove this line from `Fleans.Aspire.csproj`:
```xml
<PackageReference Include="Microsoft.Orleans.Clustering.Redis" Version="10.0.1" />
```

The `Aspire.Hosting.Orleans` package handles Orleans resource configuration at the AppHost level — it doesn't need the actual provider package.

**Step 3: Build**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds. If the Web project has compilation errors (missing types from the removed packages), add them back — the removal may not be safe if the Web project references Redis types directly. Check build output carefully.

**Step 4: Run all tests**

Run: `dotnet test src/Fleans/Fleans.sln`
Expected: All tests pass.

**Step 5: Commit**

```bash
git add -A
git commit -m "chore: remove redundant Redis packages from Web and Aspire

Aspire AppHost manages Orleans infrastructure. Web only needs
Orleans.Client for grain interfaces."
```

---

### Task 6: Final verification and cleanup

**Step 1: Full clean build**

Run: `dotnet clean src/Fleans/Fleans.sln && dotnet build src/Fleans/Fleans.sln`
Expected: Clean build with 0 errors.

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/Fleans.sln`
Expected: All 290 tests pass.

**Step 3: Review git log**

Run: `git log --oneline main..HEAD`
Expected: 5 clean commits in logical order.

**Step 4: Verify no stray changes**

Run: `git status`
Expected: Clean working tree.
