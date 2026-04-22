---
title: Add to Existing Project
description: Integrate Fleans into your existing .NET application.
---

Fleans is not yet published as NuGet packages. To add it to an existing project, reference the source directly.

## 1. Add the source

**Option A — Git submodule** (recommended for tracking upstream updates):

```bash
git submodule add https://github.com/nightBaker/fleans.git lib/fleans
```

**Option B — Copy source:**

Copy the `src/Fleans/` directory into your solution. You need these projects:

| Project | Purpose |
|---------|---------|
| `Fleans.Domain` | Domain model, aggregates, activities |
| `Fleans.Application` | Grain implementations, command/query services |
| `Fleans.Infrastructure` | BPMN converter, expression evaluation |
| `Fleans.Persistence` | EF Core storage abstractions |
| `Fleans.Persistence.Sqlite` | SQLite provider (local dev) |
| `Fleans.Persistence.PostgreSql` | PostgreSQL provider (production) |
| `Fleans.ServiceDefaults` | Shared DTOs and configuration |

## 2. Add project references

In your API/host project (`.csproj`):

```xml
<ItemGroup>
  <ProjectReference Include="..\lib\fleans\src\Fleans\Fleans.Application\Fleans.Application.csproj" />
  <ProjectReference Include="..\lib\fleans\src\Fleans\Fleans.Infrastructure\Fleans.Infrastructure.csproj" />
  <ProjectReference Include="..\lib\fleans\src\Fleans\Fleans.Persistence.Sqlite\Fleans.Persistence.Sqlite.csproj" />
</ItemGroup>
```

Replace `Fleans.Persistence.Sqlite` with `Fleans.Persistence.PostgreSql` for production deployments.

## 3. Configure Program.cs

Add Fleans services and Orleans silo configuration to your `Program.cs`:

```csharp
using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence.Sqlite;
using Orleans.EventSourcing.CustomStorage;

var builder = WebApplication.CreateBuilder(args);

// Orleans silo
builder.UseOrleans(siloBuilder =>
{
    // Use localhost clustering for development
    siloBuilder.UseLocalhostClustering();

    // In-memory storage for development (replace with Redis/SQL for production)
    siloBuilder.AddMemoryGrainStorage("PubSubStore");
    siloBuilder.UseInMemoryReminderService();

    // Event sourcing for workflow instances
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();
});

// Register Fleans services
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// Persistence — provider selected by Persistence:Provider config (default: Sqlite)
builder.AddFleansPersistence();

builder.Services.AddControllers();

var app = builder.Build();

// Ensure database schema exists
await app.EnsureDatabaseSchemaAsync();

app.MapControllers();
app.Run();
```

## 4. Add the API controller

Copy `Fleans.Api/Controllers/WorkflowController.cs` into your project, or create your own endpoints using `IWorkflowCommandService` and `IWorkflowQueryService`.

## 5. Configure persistence

Set the connection string in `appsettings.json`:

**SQLite (default):**
```json
{
  "ConnectionStrings": {
    "fleans": "Data Source=fleans.db"
  }
}
```

**PostgreSQL:**
```json
{
  "ConnectionStrings": {
    "fleans": "Host=localhost;Database=fleans;Username=fleans;Password=fleans"
  },
  "Persistence": {
    "Provider": "Postgres"
  }
}
```

See the [Persistence reference](/fleans/reference/persistence/) for full provider documentation.

## Future: NuGet packages

NuGet package publishing is planned. Once available, the setup will simplify to:

```bash
dotnet add package Fleans.Application
dotnet add package Fleans.Infrastructure
dotnet add package Fleans.Persistence.Sqlite
```

Watch the [GitHub releases](https://github.com/nightBaker/fleans/releases) for updates.
