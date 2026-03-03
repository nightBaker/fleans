# MCP Server Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Streamable HTTP MCP server so Claude Code can deploy workflows, inspect instance state, and list definitions/instances on the running Fleans engine.

**Architecture:** New `Fleans.Mcp` ASP.NET Core project registered in Aspire as an Orleans client. Uses the official `ModelContextProtocol.AspNetCore` NuGet package with `WithHttpTransport()`. Exposes 4 tools via `[McpServerToolType]` classes that delegate to existing `IWorkflowCommandService` / `IWorkflowQueryService` / `IBpmnConverter`.

**Tech Stack:** .NET 10, ASP.NET Core, ModelContextProtocol.AspNetCore NuGet, Orleans Client, Aspire

---

### Task 1: Create the Fleans.Mcp project and wire it into the solution

**Files:**
- Create: `src/Fleans/Fleans.Mcp/Fleans.Mcp.csproj`
- Create: `src/Fleans/Fleans.Mcp/Program.cs`
- Modify: `src/Fleans/Fleans.sln`
- Modify: `src/Fleans/Fleans.Aspire/Fleans.Aspire.csproj`
- Modify: `src/Fleans/Fleans.Aspire/Program.cs`

**Step 1: Create the project file**

Create `src/Fleans/Fleans.Mcp/Fleans.Mcp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Fleans.Application\Fleans.Application.csproj" />
    <ProjectReference Include="..\Fleans.Infrastructure\Fleans.Infrastructure.csproj" />
    <ProjectReference Include="..\Fleans.Persistence\Fleans.Persistence.csproj" />
    <ProjectReference Include="..\Fleans.ServiceDefaults\Fleans.ServiceDefaults.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.StackExchange.Redis" Version="13.1.1" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.1" />
    <PackageReference Include="Microsoft.Orleans.Client" Version="10.0.1" />
    <PackageReference Include="Microsoft.Orleans.Clustering.Redis" Version="10.0.1" />
    <PackageReference Include="Microsoft.Orleans.Sdk" Version="10.0.1" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.0.0" />
  </ItemGroup>

</Project>
```

**Step 2: Create the minimal Program.cs**

Create `src/Fleans/Fleans.Mcp/Program.cs`:

```csharp
using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Application + Infrastructure services (same as Web project)
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// EF Core persistence — shared SQLite file with Api silo
var sqliteConnectionString = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
builder.Services.AddEfCorePersistence(options => options.UseSqlite(sqliteConnectionString));

// Redis for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client
builder.UseOrleansClient();

// MCP server with Streamable HTTP transport
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.Run();
```

**Step 3: Add the project to the solution**

Run from `src/Fleans/`:
```bash
dotnet sln add Fleans.Mcp/Fleans.Mcp.csproj
```

**Step 4: Register in Aspire**

Add project reference to `src/Fleans/Fleans.Aspire/Fleans.Aspire.csproj`:
```xml
<ProjectReference Include="..\Fleans.Mcp\Fleans.Mcp.csproj" />
```

Add to `src/Fleans/Fleans.Aspire/Program.cs` after the Web registration block:
```csharp
// MCP = Orleans client (for Claude Code)
builder.AddProject<Projects.Fleans_Mcp>("fleans-mcp")
    .WithReference(orleans.AsClient())
    .WaitFor(fleansSilo)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);
```

**Step 5: Verify it builds**

Run: `dotnet build` from `src/Fleans/`
Expected: Build succeeds with no errors.

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Mcp/ src/Fleans/Fleans.sln src/Fleans/Fleans.Aspire/
git commit -m "feat: scaffold Fleans.Mcp project with MCP HTTP transport and Aspire registration"
```

---

### Task 2: Implement WorkflowTools (deploy_workflow + list_definitions)

**Files:**
- Create: `src/Fleans/Fleans.Mcp/Tools/WorkflowTools.cs`

**Step 1: Create the WorkflowTools class**

Create `src/Fleans/Fleans.Mcp/Tools/WorkflowTools.cs`:

```csharp
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Infrastructure.Bpmn;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tools;

[McpServerToolType]
public static class WorkflowTools
{
    [McpServerTool, Description("Deploy a BPMN XML workflow definition to the engine. Returns the process definition ID, key, version, and activity/flow counts.")]
    public static async Task<string> DeployWorkflow(
        IBpmnConverter bpmnConverter,
        IWorkflowCommandService commandService,
        [Description("The complete BPMN 2.0 XML string to deploy")] string bpmnXml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml));
        var workflow = await bpmnConverter.ConvertFromXmlAsync(stream);
        var summary = await commandService.DeployWorkflow(workflow, bpmnXml);
        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("List all deployed process definitions. Returns an array of definitions with their IDs, keys, versions, and activity counts.")]
    public static async Task<string> ListDefinitions(
        IWorkflowQueryService queryService)
    {
        var definitions = await queryService.GetAllProcessDefinitions();
        return JsonSerializer.Serialize(definitions, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

**Step 2: Verify it builds**

Run: `dotnet build` from `src/Fleans/`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Mcp/Tools/WorkflowTools.cs
git commit -m "feat: add deploy_workflow and list_definitions MCP tools"
```

---

### Task 3: Implement InstanceTools (get_instance_state + list_instances)

**Files:**
- Create: `src/Fleans/Fleans.Mcp/Tools/InstanceTools.cs`

**Step 1: Create the InstanceTools class**

Create `src/Fleans/Fleans.Mcp/Tools/InstanceTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Fleans.Application;
using Fleans.Application.QueryModels;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tools;

[McpServerToolType]
public static class InstanceTools
{
    [McpServerTool, Description("Get the full state snapshot of a workflow instance including active/completed activities, variables, condition sequences, and timestamps.")]
    public static async Task<string> GetInstanceState(
        IWorkflowQueryService queryService,
        [Description("The workflow instance ID (GUID format)")] string instanceId)
    {
        if (!Guid.TryParse(instanceId, out var id))
            throw new ArgumentException($"Invalid GUID format: {instanceId}");

        var snapshot = await queryService.GetStateSnapshot(id);
        if (snapshot is null)
            throw new ArgumentException($"Workflow instance not found: {instanceId}");

        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("List all workflow instances for a given process definition key. Returns instance IDs, status, and timestamps.")]
    public static async Task<string> ListInstances(
        IWorkflowQueryService queryService,
        [Description("The process definition key (human-readable identifier, e.g. 'my-process')")] string processDefinitionKey)
    {
        var instances = await queryService.GetInstancesByKey(processDefinitionKey);
        return JsonSerializer.Serialize(instances, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

**Step 2: Verify it builds**

Run: `dotnet build` from `src/Fleans/`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Mcp/Tools/InstanceTools.cs
git commit -m "feat: add get_instance_state and list_instances MCP tools"
```

---

### Task 4: Build, verify with Aspire, and configure Claude Code

**Files:**
- Modify: `.claude/settings.local.json`

**Step 1: Build the full solution**

Run from `src/Fleans/`:
```bash
dotnet build
```
Expected: Build succeeds with 0 errors.

**Step 2: Run Aspire to verify MCP server starts**

Run from `src/Fleans/`:
```bash
dotnet run --project Fleans.Aspire
```

Check the Aspire dashboard (typically `http://localhost:15139`). Verify:
- `fleans-mcp` resource appears and shows "Running"
- Note the port assigned to the MCP server (visible in the dashboard)

Stop Aspire with Ctrl+C.

**Step 3: Configure Claude Code MCP settings**

Find the port Aspire assigned to the MCP project and update `.claude/settings.local.json` to add the `mcpServers` section. The URL will be something like `http://localhost:{port}/mcp` where `{port}` is the port Aspire assigned.

Add to `.claude/settings.local.json`:
```json
{
  "mcpServers": {
    "fleans": {
      "url": "http://localhost:{port}/mcp"
    }
  }
}
```

**Step 4: Commit**

```bash
git add .claude/settings.local.json
git commit -m "feat: configure Claude Code MCP server connection"
```
