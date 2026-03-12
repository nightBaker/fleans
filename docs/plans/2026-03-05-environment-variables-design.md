# Environment Variables for Processes

## Problem

Workflows have no way to receive configuration values (API keys, feature flags, thresholds) from the engine environment. Users must hard-code values in BPMN scripts or pass them at start time (which isn't even supported via UI).

## Design

### Data Model

A single `EnvironmentVariablesGrain` (keyed by `0`) holds all env variable entries:

```csharp
[GenerateSerializer]
public class EnvironmentVariableEntry
{
    [Id(0)] public Guid Id { get; set; } = Guid.NewGuid();
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Value { get; set; } = string.Empty;
    [Id(3)] public string ValueType { get; set; } = "string"; // "string", "int", "float", "bool"
    [Id(4)] public bool IsSecret { get; set; }
    [Id(5)] public List<string>? ProcessKeys { get; set; } // null = all processes
}
```

**Scoping rules:**
- `ProcessKeys = null` → variable applies to all processes
- `ProcessKeys = ["process-a", "process-b"]` → only those processes

**Primitive types supported:** string, int, float (double), bool.

**EF Core entity:** Uses auto-generated `Guid Id` as primary key. `Name` has a unique index. `ProcessKeys` stored as JSON column.

### Validation

Validation happens in **two places**:

1. **Grain `Set` method** — rejects invalid entries before persisting:
   - `Name` must be non-empty
   - `ValueType` must be one of `"string"`, `"int"`, `"float"`, `"bool"`
   - `Value` must be parseable as the declared type (e.g. `"abc"` with type `"int"` is rejected)
   - Throws `ArgumentException` with a clear message on failure

2. **Web UI** — client-side validation before calling the grain:
   - Required field indicators on Name and Value
   - Type-specific input validation (numeric inputs for int/float, toggle for bool)
   - Error messages displayed inline

### Grain Interface

```csharp
public interface IEnvironmentVariablesGrain : IGrainWithIntegerKey
{
    [ReadOnly] ValueTask<List<EnvironmentVariableEntry>> GetAll();
    ValueTask Set(EnvironmentVariableEntry variable);
    ValueTask Remove(string name);
    [ReadOnly] ValueTask<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey);
}
```

`GetAll` and `GetVariablesForProcess` are `[ReadOnly]` — Orleans allows concurrent reads, so the singleton grain is not a bottleneck.

### Persistence

New `EnvironmentVariables` and `EnvironmentVariableEntries` DB tables, persisted via EF Core grain storage (same pattern as `SignalCorrelationGrainStorage`).

### Injection into Workflow

In `WorkflowInstance.StartWorkflow`, after `State.Start()` and before `ExecuteWorkflow()`:

1. Call `IEnvironmentVariablesGrain.GetVariablesForProcess(processDefinitionKey)`
2. Merge the resulting dictionary into the root variable scope as `Env`:
   `State.MergeState(rootScopeId, expandoWithEnv)` where `expandoWithEnv["Env"] = mergedDict`
3. Activities access env vars via `GetVariable(scopeId, "Env")` which returns the dictionary

This is a **snapshot** — changes to env variables after a workflow starts do not affect running instances.

### Secret Masking

Secrets are masked in **two places** in the UI:

1. **Environment Variables page** — value column shows `••••••••` with a reveal toggle (eye icon)
2. **Process Instance Variables tab** — when displaying the `Env` dictionary, values whose keys correspond to secret env variables are masked. The `IsSecret` flag is stored alongside the value in the `Env` dictionary as metadata (e.g. `Env` contains `{ "API_KEY": { "value": "abc123", "secret": true } }` or simpler: inject a companion `_EnvSecrets` set containing secret key names).

**Design choice for secret masking in Variables tab:** Inject a second variable `_EnvSecretKeys` (a `List<string>`) into the root scope alongside `Env`. The `WorkflowQueryService.FormatVariableValue` and/or the `ProcessInstance.razor` Variables tab uses this list to mask values when rendering the `Env` dictionary. The `_EnvSecretKeys` variable is prefixed with underscore to signal it's engine-internal.

### Web Admin UI

New **"Environment Variables"** page accessible from the sidebar:

- **Table columns:** Name, Value (masked for secrets, eye toggle to reveal), Type, Scope ("All processes" or comma-separated process keys), Secret, Actions (edit/delete)
- **Add/Edit dialog:** Name (text), Value (text/password), Type (dropdown), Secret (checkbox), Scope (radio: All / Selected + multi-select process keys)
- **Validation:** inline error messages for invalid type/value combinations
- Communicates with `EnvironmentVariablesGrain` directly (no HTTP API, consistent with existing Web patterns)
- **Verify Fluent UI components exist** before implementation — `FluentDialog`, `FluentSelect` with multi-select, `FluentRadioGroup`. Fall back to alternatives if needed.

### Security

- Secret values stored as **plain text** in the database — the `IsSecret` flag controls **UI display only**
- No encryption at rest
- This is a deliberate trade-off: simple, self-hosted engine where DB access implies full trust
- Clearly documented so users understand the boundary

### Not Included

- Encryption at rest
- External secret store integration (can be added later behind `ISecretResolver` interface)
- Audit log of changes
- Variable versioning
- Env var inheritance in sub-processes (child workflows get their own Env snapshot)
