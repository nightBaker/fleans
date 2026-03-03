# MCP Server for Fleans

## Goal

Add an MCP (Model Context Protocol) server so Claude Code can interact with the running Fleans workflow engine during development — deploying workflows, inspecting instance state, and listing definitions/instances.

## Decisions

- **Transport:** Streamable HTTP. Works for both local development and future remote hosting. One-line change to switch to stdio if ever needed.
- **Language:** C# — consistent with the codebase, shares service interfaces directly.
- **Connection model:** Joins the Orleans cluster as a client via Aspire service discovery.
- **Scope:** 4 tools covering operations that are NOT already exposed via the API. Operations already available via API (`start`, `message`, `signal`) are excluded — Claude Code can call those with `curl`.

## Architecture

New project: `Fleans.Mcp` — an ASP.NET Core app registered in Aspire.

```
src/Fleans/Fleans.Mcp/
├── Program.cs              # ASP.NET host: Orleans client + MCP HTTP server
├── Tools/
│   ├── WorkflowTools.cs    # deploy_workflow, list_definitions
│   └── InstanceTools.cs    # get_instance_state, list_instances
└── Fleans.Mcp.csproj
```

**Dependencies:**
- `ModelContextProtocol` NuGet — official C# MCP SDK
- `Fleans.Application` — for `IWorkflowCommandService`, `IWorkflowQueryService`
- `Fleans.Infrastructure` — for `IBpmnConverter`
- Orleans client packages

**Aspire registration:**
```csharp
builder.AddProject<Projects.Fleans_Mcp>("fleans-mcp")
    .WithReference(orleans.AsClient())
    .WaitFor(fleansSilo)
    .WithEnvironment("FLEANS_SQLITE_CONNECTION", sqliteConnectionString)
    .WithReplicas(1);
```

**Claude Code configuration** (`.claude/settings.local.json`):
```json
{
  "mcpServers": {
    "fleans": {
      "url": "http://localhost:{port}/mcp"
    }
  }
}
```

## MCP Tools

### deploy_workflow

Deploys a BPMN XML string to the engine.

- **Parameters:** `bpmn_xml: string`
- **Implementation:** Parse via `IBpmnConverter.ConvertFromXmlAsync()`, then `IWorkflowCommandService.DeployWorkflow()`
- **Returns:** `{ processDefinitionId, processDefinitionKey, version, activitiesCount, sequenceFlowsCount, deployedAt }`

### get_instance_state

Returns the full state snapshot of a workflow instance.

- **Parameters:** `instance_id: string` (Guid)
- **Implementation:** `IWorkflowQueryService.GetStateSnapshot()`
- **Returns:** `{ isStarted, isCompleted, activeActivities, completedActivities, variableStates, conditionSequences, createdAt, executionStartedAt, completedAt }`

### list_definitions

Lists all deployed process definitions.

- **Parameters:** *(none)*
- **Implementation:** `IWorkflowQueryService.GetAllProcessDefinitions()`
- **Returns:** Array of `{ processDefinitionId, processDefinitionKey, version, activitiesCount, sequenceFlowsCount, deployedAt }`

### list_instances

Lists workflow instances for a given process definition key.

- **Parameters:** `process_definition_key: string`
- **Implementation:** `IWorkflowQueryService.GetInstancesByKey()`
- **Returns:** Array of `{ instanceId, processDefinitionKey, processDefinitionVersion, isStarted, isCompleted, createdAt }`

## Error Handling

Tools throw `McpException` (from `ModelContextProtocol` namespace) for user-facing errors — the MCP SDK preserves these messages in the error response sent to Claude Code. Other exception types are caught and re-wrapped as `McpException` to prevent generic error messages. The SDK handles serialization and transport-level errors.

## Future Extensions

- Add Streamable HTTP authentication for remote deployments
- Add tools for activity-level operations (complete/fail manually) if step-by-step debugging is needed
- Support stdio transport as an alternative (tool classes are transport-agnostic)
