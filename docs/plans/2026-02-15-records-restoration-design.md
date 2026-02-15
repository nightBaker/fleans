# Design: Restore Domain Types as Records

**Goal:** Convert JSON-serialized domain types back to records with `init` setters for immutability. `ProcessDefinition` stays as a class (EF Core entity).

---

## Scope

14 types converted from `class` back to `record`:

| Layer | Types | Pattern |
|-------|-------|---------|
| Activities (10) | `Activity`, `StartEvent`, `EndEvent`, `TaskActivity`, `ScriptTask`, `Gateway`, `ConditionalGateway`, `ExclusiveGateway`, `ParallelGateway`, `ErrorEvent` | Positional `record` with constructor params |
| Sequences (3) | `SequenceFlow`, `ConditionalSequenceFlow`, `DefaultSequenceFlow` | Positional `record` with constructor params |
| Definitions (1) | `WorkflowDefinition` | `record` with `required init` properties |

**Stays as class:** `ProcessDefinition` — mapped as EF Core entity, needs mutable `set`.

---

## Why This Is Safe

- Newtonsoft.Json matches constructor parameters by name — no need for `set` accessors
- `PreserveReferencesHandling.Objects` preserves shared Activity references across JSON round-trip
- Orleans `[GenerateSerializer]` works with records
- No `with` expressions exist in the codebase
- Value equality from records is safe — activities in a workflow graph are unique by property values

---

## Activity Hierarchy

```csharp
[GenerateSerializer]
public abstract record Activity([property: Id(0)] string ActivityId)
{
    internal virtual async Task ExecuteAsync(...) { ... }
    internal virtual Task<List<Activity>> GetNextActivities(...) { ... }
}

[GenerateSerializer]
public record StartEvent(string ActivityId) : Activity(ActivityId);

[GenerateSerializer]
public record EndEvent(string ActivityId) : Activity(ActivityId);

[GenerateSerializer]
public record TaskActivity(string ActivityId) : Activity(ActivityId);

[GenerateSerializer]
public record ScriptTask(
    string ActivityId,
    [property: Id(1)] string Script,
    [property: Id(2)] string ScriptFormat = "csharp") : TaskActivity(ActivityId);

[GenerateSerializer]
public record ErrorEvent(string ActivityId) : Activity(ActivityId);

[GenerateSerializer]
public abstract record Gateway(string ActivityId) : Activity(ActivityId);

[GenerateSerializer]
public abstract record ConditionalGateway(string ActivityId) : Gateway(ActivityId);

[GenerateSerializer]
public record ExclusiveGateway(string ActivityId) : ConditionalGateway(ActivityId);

[GenerateSerializer]
public record ParallelGateway(
    string ActivityId,
    [property: Id(1)] bool IsFork) : Gateway(ActivityId);
```

---

## SequenceFlow Hierarchy

```csharp
[GenerateSerializer]
public record SequenceFlow(
    [property: Id(0)] string SequenceFlowId,
    [property: Id(1)] Activity Source,
    [property: Id(2)] Activity Target);

[GenerateSerializer]
public record ConditionalSequenceFlow(
    string SequenceFlowId,
    Activity Source,
    Activity Target,
    [property: Id(3)] string Condition) : SequenceFlow(SequenceFlowId, Source, Target);

[GenerateSerializer]
public record DefaultSequenceFlow(
    string SequenceFlowId,
    Activity Source,
    Activity Target) : SequenceFlow(SequenceFlowId, Source, Target);
```

---

## WorkflowDefinition

Uses `required init` properties (not positional) — constructed via object initializer:

```csharp
[GenerateSerializer]
public record WorkflowDefinition : IWorkflowDefinition
{
    [Id(0)]
    public required string WorkflowId { get; init; }

    [Id(1)]
    public required List<Activity> Activities { get; init; }

    [Id(2)]
    public required List<SequenceFlow> SequenceFlows { get; init; }

    [Id(3)]
    public string? ProcessDefinitionId { get; init; }

    public Activity GetActivity(string activityId)
        => Activities.First(a => a.ActivityId == activityId);
}
```

---

## Notable: ScriptTask Orleans `[Id]` renumbering

The old class-based `ScriptTask` had `Script` as `[Id(0)]` and `ScriptFormat` as `[Id(1)]`. The base `Activity` also uses `[Id(0)]` for `ActivityId` — this was an ID collision in the Orleans serialization hierarchy. The record conversion fixes this: `Script` is now `[Id(1)]`, `ScriptFormat` is `[Id(2)]`. Safe because grain state is in-memory only and EF Core persistence uses Newtonsoft.Json (matches by parameter name, not Orleans `[Id]` ordinal).

---

## Verification

```bash
cd src/Fleans && dotnet build && dotnet test
```

All 195 tests must pass, especially EF Core persistence tests verifying JSON round-trip with polymorphic types and shared references.
