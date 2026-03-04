# Environment Variables for Processes

## Problem

Workflows have no way to receive configuration values (API keys, feature flags, thresholds) from the engine environment. Users must hard-code values in BPMN scripts or pass them at start time (which isn't even supported via UI).

## Design

### Data Model

A single `EnvironmentVariablesGrain` (keyed by `0`) holds all env variable entries:

```csharp
[GenerateSerializer]
public record EnvironmentVariable
{
    [Id(0)] public string Key { get; init; }
    [Id(1)] public object Value { get; init; }        // string, int, double, bool
    [Id(2)] public string ValueType { get; init; }     // "string", "int", "float", "bool"
    [Id(3)] public bool IsSecret { get; init; }
    [Id(4)] public List<string>? ProcessKeys { get; init; } // null = all processes
}
```

**Scoping rules:**
- `ProcessKeys = null` → variable applies to all processes
- `ProcessKeys = ["process-a", "process-b"]` → only those processes

**Primitive types supported:** string, int, float (double), bool.

### Grain Interface

```csharp
public interface IEnvironmentVariablesGrain : IGrainWithIntegerKey
{
    Task<List<EnvironmentVariable>> GetAll();
    Task Set(EnvironmentVariable variable);
    Task Remove(string key);
    Task<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey);
}
```

`GetVariablesForProcess` filters entries where `ProcessKeys` is null OR contains the given key, then builds a flat `Dictionary<string, object>` from matching entries.

### Persistence

New `EnvironmentVariables` DB table, persisted via EF Core grain storage (same pattern as other grains). The grain state holds `List<EnvironmentVariable>`.

### Injection into Workflow

In `WorkflowInstance.StartWorkflow`, after `State.Start()` and before `ExecuteWorkflow()`:

1. Call `IEnvironmentVariablesGrain.GetVariablesForProcess(processDefinitionKey)`
2. Merge the resulting dictionary into the root variable scope as `Env`:
   `State.MergeState(rootScopeId, expandoWithEnv)` where `expandoWithEnv["Env"] = mergedDict`
3. Activities access env vars via `GetVariable(scopeId, "Env")` which returns the dictionary

This is a **snapshot** — changes to env variables after a workflow starts do not affect running instances.

### Web Admin UI

New **"Environment Variables"** page accessible from the sidebar:

- **Table columns:** Key, Value (masked for secrets, eye toggle to reveal), Type (dropdown), Scope ("All processes" or multi-select process keys), Secret (checkbox)
- **Actions:** Add, edit, delete rows
- **Process key selector:** Multi-select populated from deployed process definitions
- Communicates with `EnvironmentVariablesGrain` directly (no HTTP API, consistent with existing Web patterns)

### Security

- Secret values are masked in the UI by default (`••••••`), with a reveal/hide toggle
- No encryption at rest — values stored as plain text in the database
- Secret flag is purely a UI concern

### Not Included

- Encryption at rest
- Audit log of changes
- Variable versioning
- Env var inheritance in sub-processes (child workflows get their own Env snapshot)
