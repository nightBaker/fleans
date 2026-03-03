# Inclusive Gateway + Token Propagation — Design

**Date:** 2026-03-03
**Status:** Approved
**Audit ref:** Phase 3 (A3: Dynamic Join Counting), items 3.1 and 3.2

## Goal

Implement BPMN `<inclusiveGateway>` with general-purpose token propagation. Fork evaluates all conditions and activates 1..N matching paths. Join waits for exactly the tokens that were created. Supports nesting.

## Scope

**In scope:**
- InclusiveGateway fork (1..N condition paths + default)
- InclusiveGateway join (wait for all active tokens)
- Token propagation: `TokenId` on `ActivityInstanceState`, inherited through activity chains
- `GatewayForkState` — general-purpose fork tracking reusable by any gateway
- Nested inclusive gateways (fork inside a fork's branch) via `ConsumedTokenId` restoration
- Domain/Application split: gateway behavior decisions in Domain, orchestration in Application
- Refactor existing `ParallelGateway` variable cloning check into domain method
- BPMN XML parsing for `<inclusiveGateway>`
- Manual test fixtures

**Out of scope:**
- Migrating ParallelGateway to use tokens (future work — structure supports it)
- Mixed fork+join gateways (same element is both fork and join)
- `completionCondition` on inclusive join (wait for N-of-M)
- `loopCardinality` or `loopCondition` on gateways

## Architecture Decision: Token Propagation

### Why tokens?

The ParallelGateway join works by checking if ALL incoming source activities completed — because all paths are always taken. The InclusiveGateway join can't do this because only a subset of paths are activated. The join needs to know *which* paths were activated by the fork.

### Why not simpler alternatives?

| Alternative | Why rejected |
|-------------|-------------|
| Fork-join pairing via activated flow IDs | Works for single-level only. Doesn't compose for nested gateways. |
| Fork-join pairing via VariablesId | VariablesId and TokenId have different lifecycles at SubProcess boundaries. Coupling would be incorrect. |
| Static graph analysis at parse time | Complex, fragile for nested/overlapping patterns. |
| TokenId on ActivityInstanceEntry | User preference: TokenId belongs on `ActivityInstanceState` (grain level) alongside `VariablesId`. Same lifecycle, same access pattern. |

### Token lifecycle

```
Start(token=null) → Task1(null) → InclusiveFork(null)
    → Task2(token=B)  [condition1=true, vars cloned]
    → Task3(token=C)  [condition2=true, vars cloned]
    ✗ Task4           [condition3=false, no token]

Task2(B) → Task5(B) → InclusiveJoin (B arrived, waiting for C...)
Task3(C) → InclusiveJoin (B+C arrived, all done → complete)
    → Task6(token=null, parent token restored)
```

### Nested gateway token restoration

```
Fork1(null) → [branch B] → Fork2(B consumed) → [D, E] → Join2(restores B) → Join1
             [branch C] ───────────────────────────────────────────────────→ Join1
```

`GatewayForkState.ConsumedTokenId` stores the incoming token the fork killed. When the paired join completes, it restores this token on the outgoing activity so outer joins can collect it.

## Data Model

### ActivityInstanceState — add TokenId

```csharp
[Id(16)] public Guid? TokenId { get; private set; }
public void SetTokenId(Guid id) => TokenId = id;
```

- `null` for activities not downstream of an inclusive fork (backward compatible)
- Set when the inclusive fork creates branches
- Inherited by subsequent activities in the same branch

### IActivityInstanceGrain — add

```csharp
[ReadOnly] ValueTask<Guid?> GetTokenId();
ValueTask SetTokenId(Guid id);
```

### IActivityExecutionContext — add

```csharp
ValueTask<Guid?> GetTokenId();
```

### GatewayForkState (new, `Fleans.Domain/States/`)

```csharp
[GenerateSerializer]
public class GatewayForkState
{
    [Id(0)] public Guid ForkInstanceId { get; private set; }
    [Id(1)] public Guid? ConsumedTokenId { get; private set; }
    [Id(2)] public List<Guid> CreatedTokenIds { get; private set; } = [];

    public GatewayForkState(Guid forkInstanceId, Guid? consumedTokenId)
    {
        ForkInstanceId = forkInstanceId;
        ConsumedTokenId = consumedTokenId;
    }
}
```

- `ForkInstanceId` — the activity instance ID of the gateway that forked
- `ConsumedTokenId` — the incoming token the fork killed (for restoration after join)
- `CreatedTokenIds` — one token per activated branch

EF Core compatible: `List<Guid>` stores as JSON column. No dictionaries.

### WorkflowInstanceState — add

```csharp
[Id(13)] public List<GatewayForkState> GatewayForks { get; private set; } = [];

public GatewayForkState CreateGatewayFork(Guid forkInstanceId, Guid? consumedTokenId)
{
    var fork = new GatewayForkState(forkInstanceId, consumedTokenId);
    GatewayForks.Add(fork);
    return fork;
}

public GatewayForkState? FindForkByToken(Guid tokenId)
    => GatewayForks.FirstOrDefault(f => f.CreatedTokenIds.Contains(tokenId));

public void RemoveGatewayFork(Guid forkInstanceId)
    => GatewayForks.RemoveAll(f => f.ForkInstanceId == forkInstanceId);
```

### IWorkflowExecutionContext — add

```csharp
Task<GatewayForkState?> FindForkByToken(Guid tokenId);
```

## InclusiveGateway Activity

```csharp
[GenerateSerializer]
public record InclusiveGateway(
    [property: Id(0)] string ActivityId,
    [property: Id(1)] bool IsFork
) : ConditionalGateway(ActivityId)
{
    internal override bool IsJoinGateway => !IsFork;
    internal override bool CreatesNewTokensOnFork => IsFork;
    internal override bool ClonesVariablesOnFork => IsFork;
}
```

### Fork behavior (`IsFork = true`)

**ExecuteAsync:** Same as ExclusiveGateway — collect `ConditionalSequenceFlow`, emit `AddConditionsCommand`.

**SetConditionResult (override):** Wait for ALL conditions to evaluate. Never short-circuit on first `true`. Only return `true` when every condition has `IsEvaluated = true`.

```csharp
internal override async Task<bool> SetConditionResult(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    string conditionSequenceFlowId,
    bool result,
    IWorkflowDefinition definition)
{
    var activityInstanceId = await activityContext.GetActivityInstanceId();
    await workflowContext.SetConditionSequenceResult(activityInstanceId, conditionSequenceFlowId, result);

    var sequences = await workflowContext.GetConditionSequenceStates();
    if (!sequences.TryGetValue(activityInstanceId, out var mySequences))
        return false;

    if (!mySequences.All(s => s.IsEvaluated))
        return false;

    if (mySequences.Any(s => s.Result))
        return true;

    var hasDefault = definition.SequenceFlows
        .OfType<DefaultSequenceFlow>()
        .Any(sf => sf.Source.ActivityId == ActivityId);

    if (!hasDefault)
        throw new InvalidOperationException(
            $"InclusiveGateway {ActivityId}: all conditions false and no default flow");

    return true;
}
```

**GetNextActivities:** Return all flows where `Result = true`. If none true, return default flow.

```csharp
internal override async Task<List<Activity>> GetNextActivities(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var sequencesState = await workflowContext.GetConditionSequenceStates();
    var activityInstanceId = await activityContext.GetActivityInstanceId();
    if (!sequencesState.TryGetValue(activityInstanceId, out var activitySequencesState))
        activitySequencesState = [];

    var trueTargets = activitySequencesState
        .Where(x => x.Result)
        .Select(x => definition.SequenceFlows
            .First(sf => sf.SequenceFlowId == x.ConditionalSequenceFlowId).Target)
        .ToList();

    if (trueTargets.Count > 0)
        return trueTargets;

    var defaultFlow = definition.SequenceFlows
        .OfType<DefaultSequenceFlow>()
        .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

    if (defaultFlow is not null)
        return [defaultFlow.Target];

    throw new InvalidOperationException(
        $"InclusiveGateway {ActivityId}: no true condition and no default flow");
}
```

### Join behavior (`IsFork = false`)

**ExecuteAsync:** Check if all expected tokens have arrived. Computed by scanning completed sources — same pattern as `ParallelGateway.AllIncomingPathsCompleted()`.

```csharp
internal override async Task<List<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

    if (await AllExpectedTokensArrived(workflowContext, definition))
        await activityContext.Complete();

    return commands;
}

private async Task<bool> AllExpectedTokensArrived(
    IWorkflowExecutionContext workflowContext, IWorkflowDefinition definition)
{
    var incomingFlows = definition.SequenceFlows.Where(sf => sf.Target == this).ToList();
    var completedActivities = await workflowContext.GetCompletedActivities();

    var arrivedTokens = new HashSet<Guid>();
    foreach (var flow in incomingFlows)
    {
        foreach (var completed in completedActivities)
        {
            if (await completed.GetActivityId() == flow.Source.ActivityId)
            {
                var tokenId = await completed.GetTokenId();
                if (tokenId.HasValue)
                    arrivedTokens.Add(tokenId.Value);
            }
        }
    }

    if (arrivedTokens.Count == 0)
        return false;

    var forkState = await workflowContext.FindForkByToken(arrivedTokens.First());
    if (forkState == null)
        return false;

    return forkState.CreatedTokenIds.All(t => arrivedTokens.Contains(t));
}
```

**GetNextActivities:** Return all outgoing flows.

**GetRestoredTokenAfterJoin:** Restore the consumed parent token.

```csharp
internal override Guid? GetRestoredTokenAfterJoin(GatewayForkState? forkState)
    => forkState?.ConsumedTokenId;
```

## Domain/Application Split — Virtual Methods on Gateway

Add virtual methods to `Gateway` (or `Activity`) that express domain behavior. The application layer calls these instead of `is` pattern-matching:

```csharp
// Gateway.cs
internal virtual bool CreatesNewTokensOnFork => false;
internal virtual bool ClonesVariablesOnFork => false;
internal virtual Guid? GetRestoredTokenAfterJoin(GatewayForkState? forkState) => null;
```

Overrides:
- `ParallelGateway { IsFork: true }` → `ClonesVariablesOnFork => true`
- `InclusiveGateway { IsFork: true }` → `CreatesNewTokensOnFork => true`, `ClonesVariablesOnFork => true`
- `InclusiveGateway { IsFork: false }` → `GetRestoredTokenAfterJoin(f) => f?.ConsumedTokenId`

### Refactored CreateNextActivityEntry

```csharp
private async Task<ActivityInstanceEntry> CreateNextActivityEntry(
    Activity sourceActivity, IActivityInstanceGrain sourceInstance,
    Activity nextActivity, Guid? scopeId)
{
    var sourceVariablesId = await sourceInstance.GetVariablesStateId();

    // Domain decides variable behavior
    var variablesId = sourceActivity.ClonesVariablesOnFork
        ? State.AddCloneOfVariableState(sourceVariablesId)
        : sourceVariablesId;

    var newId = Guid.NewGuid();
    var newInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(newId);
    await newInstance.SetVariablesId(variablesId);
    await newInstance.SetActivity(nextActivity.ActivityId, nextActivity.GetType().Name);

    // Token propagation
    if (sourceActivity.CreatesNewTokensOnFork)
    {
        var sourceTokenId = await sourceInstance.GetTokenId();
        var newTokenId = Guid.NewGuid();
        await newInstance.SetTokenId(newTokenId);

        var forkState = State.GatewayForks.FirstOrDefault(
            f => f.ForkInstanceId == /* sourceEntry.ActivityInstanceId */);
        if (forkState == null)
        {
            forkState = State.CreateGatewayFork(/* sourceEntry.ActivityInstanceId */, sourceTokenId);
        }
        forkState.CreatedTokenIds.Add(newTokenId);
    }
    else
    {
        // Inherit source's token
        var sourceTokenId = await sourceInstance.GetTokenId();
        if (sourceTokenId.HasValue)
            await newInstance.SetTokenId(sourceTokenId.Value);

        // Restore parent token after join
        if (sourceActivity is Gateway gw)
        {
            var forkState = sourceTokenId.HasValue
                ? State.FindForkByToken(sourceTokenId.Value)
                : null;
            var restoredToken = gw.GetRestoredTokenAfterJoin(forkState);
            if (restoredToken.HasValue)
            {
                await newInstance.SetTokenId(restoredToken.Value);
                State.RemoveGatewayFork(forkState!.ForkInstanceId);
            }
        }
    }

    return new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id, scopeId);
}
```

## ConditionalGateway Change

Make `SetConditionResult` virtual:

```csharp
// ConditionalGateway.cs
internal virtual async Task<bool> SetConditionResult(...)
{
    // Existing behavior unchanged: short-circuit on first true
}
```

`InclusiveGateway` overrides it (see fork behavior above).

## BPMN Parsing

In `BpmnConverter.ParseActivities`, add `<inclusiveGateway>` parsing. Same fork/join detection heuristic as `<parallelGateway>`:

```csharp
foreach (var gateway in scopeElement.Elements(Bpmn + "inclusiveGateway"))
{
    var id = GetId(gateway);
    var defaultFlowId = gateway.Attribute("default")?.Value;
    if (defaultFlowId is not null)
        defaultFlowIds.Add(defaultFlowId);

    var incomingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
        .Count(sf => sf.Attribute("targetRef")?.Value == id);
    var outgoingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
        .Count(sf => sf.Attribute("sourceRef")?.Value == id);

    bool isFork;
    if (outgoingCount > incomingCount) isFork = true;
    else if (incomingCount > outgoingCount) isFork = false;
    else if (incomingCount <= 1) isFork = true;
    else throw new InvalidOperationException(
        $"Inclusive gateway '{id}' has {incomingCount} incoming and {outgoingCount} outgoing flows. " +
        "Mixed inclusive gateways are not supported.");

    var activity = new InclusiveGateway(id, IsFork: isFork);
    activities.Add(activity);
    activityMap[id] = activity;
}
```

## Cleanup

- **Join completion:** `State.RemoveGatewayFork(forkInstanceId)` when join creates outgoing entries
- **Workflow completion:** `State.GatewayForks.Clear()` in `WorkflowInstanceState.Complete()`
- **Scope cancellation:** if a gateway's scope is cancelled, remove its fork state

## Testing

### Application-level tests (`InclusiveGatewayTests.cs`)

1. Fork with 2 of 3 conditions true → both branches execute → join waits → transitions to next
2. Fork with 1 condition true → single branch → join proceeds immediately
3. Fork with no conditions true + default flow → takes default path
4. Fork with no conditions true + no default → throws
5. Variable isolation — each branch gets cloned variables
6. Variable merge at join — variables from all branches merged
7. Token propagation through multi-activity chain (Fork → Task → Task → Join)
8. Nested inclusive gateways — inner fork/join inside outer branch, token restoration works
9. Inclusive gateway inside subprocess — scoping works

### Domain tests

- `InclusiveGateway.SetConditionResult` waits for all conditions (no short-circuit)
- `InclusiveGateway.GetNextActivities` returns all true-condition targets
- `InclusiveGateway.GetNextActivities` returns default when all false
- `AllExpectedTokensArrived` returns false when tokens pending, true when all arrived

### BPMN parsing tests

- Parse `<inclusiveGateway>` with fork detection
- Parse `<inclusiveGateway>` with join detection
- Parse `default` attribute

### Manual test fixtures

- `tests/manual/14-inclusive-gateway/parallel-conditions.bpmn`
- `tests/manual/14-inclusive-gateway/default-flow.bpmn`
- `tests/manual/14-inclusive-gateway/nested-inclusive.bpmn`
- `tests/manual/14-inclusive-gateway/test-plan.md`

## Files Changed

### New files

| File | Purpose |
|------|---------|
| `Fleans.Domain/Activities/InclusiveGateway.cs` | Inclusive gateway with fork/join behavior |
| `Fleans.Domain/States/GatewayForkState.cs` | Tracks which tokens a fork created + consumed parent token |
| `Fleans.Application.Tests/InclusiveGatewayTests.cs` | Application-level tests |
| `Fleans.Domain.Tests/InclusiveGatewayActivityTests.cs` | Domain-level unit tests |
| `Fleans.Infrastructure.Tests/BpmnConverter/InclusiveGatewayParsingTests.cs` | Parsing tests |
| `tests/manual/14-inclusive-gateway/` | Manual test fixtures |

### Modified files

| File | Change |
|------|--------|
| `ActivityInstanceState.cs` | Add `TokenId` field + `SetTokenId()` |
| `IActivityInstanceGrain.cs` | Add `GetTokenId()`, `SetTokenId()` |
| `IActivityExecutionContext.cs` | Add `GetTokenId()` |
| `ActivityInstance.cs` (grain) | Implement token methods |
| `WorkflowInstanceState.cs` | Add `List<GatewayForkState>`, `CreateGatewayFork`, `FindForkByToken`, `RemoveGatewayFork` |
| `IWorkflowExecutionContext.cs` | Add `FindForkByToken()` |
| `Gateway.cs` | Add virtual `CreatesNewTokensOnFork`, `ClonesVariablesOnFork`, `GetRestoredTokenAfterJoin` |
| `ParallelGateway.cs` | Override `ClonesVariablesOnFork => IsFork` |
| `ConditionalGateway.cs` | Make `SetConditionResult` virtual |
| `WorkflowInstance.Execution.cs` | Token inheritance/creation/restoration in `CreateNextActivityEntry`, use domain properties instead of `is` checks |
| `WorkflowInstance.ActivityLifecycle.cs` | Handle InclusiveGateway in `CompleteConditionSequence` (different log messages for non-short-circuit) |
| `WorkflowInstance.Logging.cs` | New log messages for inclusive gateway + token propagation |
| `WorkflowInstance.StateFacade.cs` | Expose `FindForkByToken` |
| `BpmnConverter.cs` | Parse `<inclusiveGateway>` |
| `README.md` | Update BPMN coverage table |
