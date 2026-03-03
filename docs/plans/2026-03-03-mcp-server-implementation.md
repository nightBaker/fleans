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

**Step 2: Create Program.cs**

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

// EF Core persistence — shared SQLite file with Api silo.
// Note: grain storage registrations from AddEfCorePersistence are unused in this
// Orleans client, but splitting the registration is a future refactor.
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

// Ensure EF Core database exists (dev only — use migrations in production).
// Wrapped in try-catch: Api silo may have already created the tables in the shared SQLite file.
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
    using var db = dbFactory.CreateDbContext();
    try { db.Database.EnsureCreated(); }
    catch (Microsoft.Data.Sqlite.SqliteException) { /* tables already created by Api */ }
}

app.MapDefaultEndpoints();
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
using Fleans.Infrastructure.Bpmn;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tools;

[McpServerToolType]
public static class WorkflowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Deploy a BPMN XML workflow definition to the engine. Returns the process definition ID, key, version, and activity/flow counts.")]
    public static async Task<string> DeployWorkflow(
        IBpmnConverter bpmnConverter,
        IWorkflowCommandService commandService,
        [Description("The complete BPMN 2.0 XML string to deploy")] string bpmnXml)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml));
            var workflow = await bpmnConverter.ConvertFromXmlAsync(stream);
            var summary = await commandService.DeployWorkflow(workflow, bpmnXml);
            return JsonSerializer.Serialize(summary, JsonOptions);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to deploy workflow: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("List all deployed process definitions. Returns an array of definitions with their IDs, keys, versions, and activity counts.")]
    public static async Task<string> ListDefinitions(
        IWorkflowQueryService queryService)
    {
        var definitions = await queryService.GetAllProcessDefinitions();
        return JsonSerializer.Serialize(definitions, JsonOptions);
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
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tools;

[McpServerToolType]
public static class InstanceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Get the full state snapshot of a workflow instance including active/completed activities, variables, condition sequences, and timestamps.")]
    public static async Task<string> GetInstanceState(
        IWorkflowQueryService queryService,
        [Description("The workflow instance ID (GUID format)")] string instanceId)
    {
        if (!Guid.TryParse(instanceId, out var id))
            throw new McpException($"Invalid GUID format: {instanceId}");

        var snapshot = await queryService.GetStateSnapshot(id);
        if (snapshot is null)
            throw new McpException($"Workflow instance not found: {instanceId}");

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    [McpServerTool, Description("List all workflow instances for a given process definition key. Returns instance IDs, status, and timestamps.")]
    public static async Task<string> ListInstances(
        IWorkflowQueryService queryService,
        [Description("The process definition key (human-readable identifier, e.g. 'my-process')")] string processDefinitionKey)
    {
        var instances = await queryService.GetInstancesByKey(processDefinitionKey);
        return JsonSerializer.Serialize(instances, JsonOptions);
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

### Task 4: Add integration tests for MCP tool discovery and DI wiring

**Files:**
- Create: `src/Fleans/Fleans.Mcp.Tests/Fleans.Mcp.Tests.csproj`
- Create: `src/Fleans/Fleans.Mcp.Tests/McpToolRegistrationTests.cs`
- Modify: `src/Fleans/Fleans.sln`

**Step 1: Create the test project**

Create `src/Fleans/Fleans.Mcp.Tests/Fleans.Mcp.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />
    <PackageReference Include="MSTest.TestAdapter" Version="3.8.3" />
    <PackageReference Include="MSTest.TestFramework" Version="3.8.3" />
    <PackageReference Include="NSubstitute" Version="5.3.0" />
    <PackageReference Include="ModelContextProtocol" Version="1.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Fleans.Mcp\Fleans.Mcp.csproj" />
  </ItemGroup>

</Project>
```

Note: Check existing test projects for the exact MSTest / NSubstitute versions in use and match them. The versions above are placeholders — use whatever the existing test projects use.

**Step 2: Create the test class**

Create `src/Fleans/Fleans.Mcp.Tests/McpToolRegistrationTests.cs`:

```csharp
using Fleans.Mcp.Tools;
using ModelContextProtocol.Server;
using System.Reflection;

namespace Fleans.Mcp.Tests;

[TestClass]
public class McpToolRegistrationTests
{
    [TestMethod]
    public void AllToolClasses_HaveMcpServerToolTypeAttribute()
    {
        var toolTypes = typeof(WorkflowTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();

        Assert.IsTrue(toolTypes.Count >= 2, $"Expected at least 2 tool classes, found {toolTypes.Count}");
        CollectionAssert.Contains(toolTypes, typeof(WorkflowTools));
        CollectionAssert.Contains(toolTypes, typeof(InstanceTools));
    }

    [TestMethod]
    public void WorkflowTools_ExposesExpectedTools()
    {
        var tools = typeof(WorkflowTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.AreEqual(2, tools.Count, $"Expected 2 tools, found: {string.Join(", ", tools)}");
        CollectionAssert.Contains(tools, nameof(WorkflowTools.DeployWorkflow));
        CollectionAssert.Contains(tools, nameof(WorkflowTools.ListDefinitions));
    }

    [TestMethod]
    public void InstanceTools_ExposesExpectedTools()
    {
        var tools = typeof(InstanceTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.AreEqual(2, tools.Count, $"Expected 2 tools, found: {string.Join(", ", tools)}");
        CollectionAssert.Contains(tools, nameof(InstanceTools.GetInstanceState));
        CollectionAssert.Contains(tools, nameof(InstanceTools.ListInstances));
    }

    [TestMethod]
    public void GetInstanceState_ThrowsMcpException_ForInvalidGuid()
    {
        var ex = Assert.ThrowsExceptionAsync<ModelContextProtocol.McpException>(
            () => InstanceTools.GetInstanceState(null!, "not-a-guid"));

        Assert.IsTrue(ex.Result.Message.Contains("Invalid GUID format"));
    }
}
```

**Step 3: Add test project to solution**

Run from `src/Fleans/`:
```bash
dotnet sln add Fleans.Mcp.Tests/Fleans.Mcp.Tests.csproj
```

**Step 4: Run tests**

Run: `dotnet test src/Fleans/Fleans.Mcp.Tests/`
Expected: All 4 tests pass.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Mcp.Tests/ src/Fleans/Fleans.sln
git commit -m "test: add MCP tool registration and validation tests"
```

---

### Task 5: Build, verify with Aspire, and configure Claude Code

**Files:**
- Modify: `.claude/settings.local.json`

**Step 1: Build the full solution**

Run from `src/Fleans/`:
```bash
dotnet build
```
Expected: Build succeeds with 0 errors.

**Step 2: Run all tests**

Run from `src/Fleans/`:
```bash
dotnet test
```
Expected: All tests pass (including the new MCP tests).

**Step 3: Run Aspire to verify MCP server starts**

Run from `src/Fleans/`:
```bash
dotnet run --project Fleans.Aspire
```

Check the Aspire dashboard (typically `http://localhost:15139`). Verify:
- `fleans-mcp` resource appears and shows "Running"
- Note the port assigned to the MCP server (visible in the dashboard)

Stop Aspire with Ctrl+C.

**Step 4: Configure Claude Code MCP settings**

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

**Step 5: Commit**

```bash
git add .claude/settings.local.json
git commit -m "feat: configure Claude Code MCP server connection"
```
