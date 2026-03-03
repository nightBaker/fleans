# Inclusive Gateway + Token Propagation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement BPMN Inclusive Gateway with token-based branch tracking for fork/join synchronization.

**Architecture:** InclusiveGateway extends ConditionalGateway with an IsFork flag. Fork evaluates all conditions (no short-circuit) and creates new tokens per activated branch. Join checks if all expected tokens have arrived by scanning completed sources. GatewayForkState tracks created tokens and the consumed parent token for nested gateway restoration.

**Tech Stack:** .NET 9, Orleans 9, MSTest, Orleans.TestingHost, EF Core (state storage)

**Design doc:** `docs/plans/2026-03-03-inclusive-gateway-design.md`

---

### Task 1: GatewayForkState Domain Class

**Files:**
- Create: `src/Fleans/Fleans.Domain/States/GatewayForkState.cs`

**Step 1: Create the GatewayForkState class**

```csharp
namespace Fleans.Domain.States;

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

    private GatewayForkState() { }
}
```

**Step 2: Verify it builds**

Run: `dotnet build src/Fleans/Fleans.Domain/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/GatewayForkState.cs
git commit -m "feat: add GatewayForkState domain class for fork token tracking"
```

---

### Task 2: Add TokenId to ActivityInstanceState + Grain Interface

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/ActivityInstanceState.cs` (after line 54, `[Id(15)]` is the highest)
- Modify: `src/Fleans/Fleans.Domain/IActivityExecutionContext.cs` (line 17, before closing brace)
- Modify: `src/Fleans/Fleans.Application/Grains/IActivityInstanceGrain.cs` (after line 42)
- Modify: `src/Fleans/Fleans.Application/Grains/ActivityInstance.cs` (after line 107)

**Step 1: Add TokenId to ActivityInstanceState**

In `ActivityInstanceState.cs`, after `SetMultiInstanceTotal` (line 133):

```csharp
[Id(16)]
public Guid? TokenId { get; private set; }

public void SetTokenId(Guid id) => TokenId = id;
```

**Step 2: Add GetTokenId to IActivityExecutionContext**

In `IActivityExecutionContext.cs`, after `IsCompleted()` (line 13):

```csharp
ValueTask<Guid?> GetTokenId();
```

**Step 3: Add to IActivityInstanceGrain**

In `IActivityInstanceGrain.cs`, after `SetMultiInstanceTotal` (line 43):

```csharp
[ReadOnly]
new ValueTask<Guid?> GetTokenId();

ValueTask SetTokenId(Guid id);
```

**Step 4: Implement in ActivityInstance grain**

In `ActivityInstance.cs`, after `SetMultiInstanceTotal` method (around line 107):

```csharp
public ValueTask<Guid?> GetTokenId() => ValueTask.FromResult(State.TokenId);

public async ValueTask SetTokenId(Guid id)
{
    State.SetTokenId(id);
    await _state.WriteStateAsync();
}
```

**Step 5: Verify it builds**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/ActivityInstanceState.cs \
  src/Fleans/Fleans.Domain/IActivityExecutionContext.cs \
  src/Fleans/Fleans.Application/Grains/IActivityInstanceGrain.cs \
  src/Fleans/Fleans.Application/Grains/ActivityInstance.cs
git commit -m "feat: add TokenId to ActivityInstanceState and grain interface"
```

---

### Task 3: Add GatewayForks to WorkflowInstanceState + IWorkflowExecutionContext

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs` (highest `[Id]` is `[Id(12)]` at line 42)
- Modify: `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs` (line 16, before closing brace)
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.StateFacade.cs` (line 52, before closing brace)

**Step 1: Add GatewayForks list and methods to WorkflowInstanceState**

After `ParentActivityId` property (line 45), add the new property:

```csharp
[Id(13)]
public List<GatewayForkState> GatewayForks { get; private set; } = [];
```

Before the closing brace (line 194), add methods:

```csharp
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

**Step 2: Add FindForkByToken to IWorkflowExecutionContext**

In `IWorkflowExecutionContext.cs`, before the closing brace:

```csharp
ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId);
```

**Step 3: Implement in WorkflowInstance.StateFacade.cs**

Before the closing brace:

```csharp
public ValueTask<GatewayForkState?> FindForkByToken(Guid tokenId)
    => ValueTask.FromResult(State.FindForkByToken(tokenId));
```

**Step 4: Verify it builds**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs \
  src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs \
  src/Fleans/Fleans.Application/Grains/WorkflowInstance.StateFacade.cs
git commit -m "feat: add GatewayForks to WorkflowInstanceState and execution context"
```

---

### Task 4: Add Virtual Gateway Methods + Refactor ParallelGateway

This refactors the domain/application split so gateway behavior is expressed via domain methods.

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/Gateway.cs` (currently 4 lines)
- Modify: `src/Fleans/Fleans.Domain/Activities/ParallelGateway.cs` (add override)
- Modify: `src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs` (make SetConditionResult virtual)

**Step 1: Add virtual methods to Gateway**

Replace the current `Gateway.cs` content (it's just 4 lines) with:

```csharp
using Fleans.Domain.States;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Gateway(string ActivityId) : Activity(ActivityId)
{
    internal virtual bool CreatesNewTokensOnFork => false;
    internal virtual bool ClonesVariablesOnFork => false;
    internal virtual Guid? GetRestoredTokenAfterJoin(GatewayForkState? forkState) => null;
}
```

**Step 2: Override ClonesVariablesOnFork in ParallelGateway**

In `ParallelGateway.cs`, after `IsJoinGateway` (line 13), add:

```csharp
internal override bool ClonesVariablesOnFork => IsFork;
```

**Step 3: Make SetConditionResult virtual in ConditionalGateway**

In `ConditionalGateway.cs` line 8, change:

```csharp
internal async Task<bool> SetConditionResult(
```

to:

```csharp
internal virtual async Task<bool> SetConditionResult(
```

**Step 4: Verify it builds**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 5: Run existing tests to verify no regressions**

Run: `dotnet test src/Fleans/`
Expected: All existing tests pass

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/Gateway.cs \
  src/Fleans/Fleans.Domain/Activities/ParallelGateway.cs \
  src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs
git commit -m "refactor: add virtual gateway methods for domain-driven fork/join behavior"
```

---

### Task 5: Refactor CreateNextActivityEntry to Use Domain Methods

Replace the `is ParallelGateway { IsFork: true }` pattern check with the domain property.

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs` (line 184-186)

**Step 1: Replace the variable cloning check**

In `CreateNextActivityEntry` (line 184-186), change:

```csharp
var variablesId = sourceActivity is ParallelGateway { IsFork: true }
    ? State.AddCloneOfVariableState(sourceVariablesId)
    : sourceVariablesId;
```

to:

```csharp
var variablesId = sourceActivity is Gateway { ClonesVariablesOnFork: true }
    ? State.AddCloneOfVariableState(sourceVariablesId)
    : sourceVariablesId;
```

**Step 2: Run existing tests to verify no regressions**

Run: `dotnet test src/Fleans/`
Expected: All existing tests pass (ParallelGateway still clones variables)

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs
git commit -m "refactor: use domain ClonesVariablesOnFork instead of is-check"
```

---

### Task 6: InclusiveGateway Domain Class — Fork Tests

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/InclusiveGateway.cs`
- Create: `src/Fleans/Fleans.Domain.Tests/InclusiveGatewayActivityTests.cs`

**Step 1: Write failing domain tests for fork behavior**

Create `InclusiveGatewayActivityTests.cs`. Follow the pattern from `ConditionalGatewayActivityTests.cs` — use `ActivityTestHelper` and NSubstitute mocking.

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class InclusiveGatewayActivityTests
{
    [TestMethod]
    public async Task SetConditionResult_ShouldNotShortCircuit_WhenFirstConditionIsTrue()
    {
        // Arrange — gateway with 2 conditions, only first evaluated so far
        var gateway = new InclusiveGateway("ig", IsFork: true);
        var (workflowContext, activityContext, definition) =
            ActivityTestHelper.CreateGatewayTestContext(gateway, conditionCount: 2);

        // Set up: first condition evaluated (true), second still pending
        var sequences = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [ActivityTestHelper.DefaultActivityInstanceId] = new[]
            {
                new ConditionSequenceState("seq1", ActivityTestHelper.DefaultActivityInstanceId, Guid.NewGuid()) { },
                new ConditionSequenceState("seq2", ActivityTestHelper.DefaultActivityInstanceId, Guid.NewGuid())
            }
        };
        // First is evaluated+true, second is not evaluated
        sequences[ActivityTestHelper.DefaultActivityInstanceId][0].SetResult(true);

        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(sequences));

        // Act
        var result = await gateway.SetConditionResult(
            workflowContext, activityContext, "seq1", true, definition);

        // Assert — should NOT short-circuit; second condition still pending
        Assert.IsFalse(result, "Should not complete until all conditions evaluated");
    }

    [TestMethod]
    public async Task SetConditionResult_ShouldReturnTrue_WhenAllConditionsEvaluated()
    {
        // Arrange
        var gateway = new InclusiveGateway("ig", IsFork: true);
        var (workflowContext, activityContext, definition) =
            ActivityTestHelper.CreateGatewayTestContext(gateway, conditionCount: 2);

        // Both conditions evaluated: first true, second false
        var sequences = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [ActivityTestHelper.DefaultActivityInstanceId] = new[]
            {
                new ConditionSequenceState("seq1", ActivityTestHelper.DefaultActivityInstanceId, Guid.NewGuid()),
                new ConditionSequenceState("seq2", ActivityTestHelper.DefaultActivityInstanceId, Guid.NewGuid())
            }
        };
        sequences[ActivityTestHelper.DefaultActivityInstanceId][0].SetResult(true);
        sequences[ActivityTestHelper.DefaultActivityInstanceId][1].SetResult(false);

        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(sequences));

        // Act
        var result = await gateway.SetConditionResult(
            workflowContext, activityContext, "seq2", false, definition);

        // Assert — all evaluated, at least one true → decision made
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnAllTrueTargets()
    {
        // Arrange — gateway with 3 outgoing conditional flows, 2 are true
        var gateway = new InclusiveGateway("ig", IsFork: true);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var end3 = new EndEvent("end3");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = [gateway, end1, end2, end3],
            SequenceFlows =
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "cond1"),
                new ConditionalSequenceFlow("seq2", gateway, end2, "cond2"),
                new ConditionalSequenceFlow("seq3", gateway, end3, "cond3")
            ]
        };

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = Substitute.For<IWorkflowExecutionContext>();
        var activityContext = Substitute.For<IActivityExecutionContext>();
        activityContext.GetActivityInstanceId().Returns(ValueTask.FromResult(activityInstanceId));

        // seq1=true, seq2=true, seq3=false
        var sequences = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = new[]
            {
                new ConditionSequenceState("seq1", activityInstanceId, Guid.NewGuid()),
                new ConditionSequenceState("seq2", activityInstanceId, Guid.NewGuid()),
                new ConditionSequenceState("seq3", activityInstanceId, Guid.NewGuid())
            }
        };
        sequences[activityInstanceId][0].SetResult(true);
        sequences[activityInstanceId][1].SetResult(true);
        sequences[activityInstanceId][2].SetResult(false);

        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(sequences));

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — should return end1 and end2 (the two true targets)
        Assert.AreEqual(2, nextActivities.Count);
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "end1"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "end2"));
        Assert.IsFalse(nextActivities.Any(a => a.ActivityId == "end3"));
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnDefault_WhenAllConditionsFalse()
    {
        // Arrange
        var gateway = new InclusiveGateway("ig", IsFork: true);
        var end1 = new EndEvent("end1");
        var endDefault = new EndEvent("endDefault");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = [gateway, end1, endDefault],
            SequenceFlows =
            [
                new ConditionalSequenceFlow("seq1", gateway, end1, "cond1"),
                new DefaultSequenceFlow("seqDefault", gateway, endDefault)
            ]
        };

        var activityInstanceId = Guid.NewGuid();
        var workflowContext = Substitute.For<IWorkflowExecutionContext>();
        var activityContext = Substitute.For<IActivityExecutionContext>();
        activityContext.GetActivityInstanceId().Returns(ValueTask.FromResult(activityInstanceId));

        var sequences = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = new[]
            {
                new ConditionSequenceState("seq1", activityInstanceId, Guid.NewGuid())
            }
        };
        sequences[activityInstanceId][0].SetResult(false);

        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(sequences));

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — should return default target
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("endDefault", nextActivities[0].ActivityId);
    }
}
```

Note: The exact mocking setup depends on `ActivityTestHelper` — the implementer should check `ConditionalGatewayActivityTests.cs` and adapt. The tests above show the logic to test; adapt mock setup to match the existing helper pattern.

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "InclusiveGateway"`
Expected: FAIL — `InclusiveGateway` class doesn't exist yet

**Step 3: Create the InclusiveGateway class**

Create `src/Fleans/Fleans.Domain/Activities/InclusiveGateway.cs`:

```csharp
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record InclusiveGateway(
    [property: Id(0)] string ActivityId,
    [property: Id(1)] bool IsFork
) : ConditionalGateway(ActivityId)
{
    internal override bool IsJoinGateway => !IsFork;
    internal override bool CreatesNewTokensOnFork => IsFork;
    internal override bool ClonesVariablesOnFork => IsFork;

    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        if (IsFork)
        {
            // Fork: collect conditional flows and emit AddConditionsCommand (same as ExclusiveGateway)
            var activityId = await activityContext.GetActivityId();
            var sequences = definition.SequenceFlows.OfType<ConditionalSequenceFlow>()
                .Where(sf => sf.Source.ActivityId == activityId)
                .ToArray();

            if (sequences.Length == 0)
            {
                await activityContext.Complete();
                return commands;
            }

            var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
            var evaluations = sequences.Select(s => new ConditionEvaluation(s.SequenceFlowId, s.Condition)).ToList();
            commands.Add(new AddConditionsCommand(sequenceFlowIds, evaluations));
        }
        else
        {
            // Join: check if all expected tokens have arrived
            if (await AllExpectedTokensArrived(workflowContext, definition))
                await activityContext.Complete();
        }

        return commands;
    }

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

        // Wait for ALL conditions — never short-circuit on first true
        if (!mySequences.All(s => s.IsEvaluated))
            return false;

        if (mySequences.Any(s => s.Result))
            return true;

        // All false — need default flow
        var hasDefault = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == ActivityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                $"InclusiveGateway {ActivityId}: all conditions false and no default flow");

        return true;
    }

    internal override async Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        if (!IsFork)
        {
            // Join: return all outgoing flows
            return definition.SequenceFlows.Where(sf => sf.Source == this)
                .Select(flow => flow.Target)
                .ToList();
        }

        // Fork: return all flows where condition was true
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

    internal override Guid? GetRestoredTokenAfterJoin(GatewayForkState? forkState)
        => forkState?.ConsumedTokenId;

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
}
```

**Step 4: Run the domain tests**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "InclusiveGateway"`
Expected: All 4 tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/InclusiveGateway.cs \
  src/Fleans/Fleans.Domain.Tests/InclusiveGatewayActivityTests.cs
git commit -m "feat: add InclusiveGateway domain class with fork/join logic and tests"
```

---

### Task 7: Token Propagation in CreateNextActivityEntry

This is the core execution change — adding token creation at fork, token inheritance, and token restoration after join.

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs` (lines 179-195, `CreateNextActivityEntry`)
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs` (add new EventIds 1047+)

**Step 1: Add logging declarations**

In `WorkflowInstance.Logging.cs`, before the closing brace, add:

```csharp
[LoggerMessage(EventId = 1047, Level = LogLevel.Information,
    Message = "Inclusive fork '{ActivityId}': created token {TokenId} for branch")]
private partial void LogTokenCreated(string activityId, Guid tokenId);

[LoggerMessage(EventId = 1048, Level = LogLevel.Debug,
    Message = "Token {TokenId} inherited by activity '{ActivityId}'")]
private partial void LogTokenInherited(Guid tokenId, string activityId);

[LoggerMessage(EventId = 1049, Level = LogLevel.Information,
    Message = "Token {TokenId} restored after join '{ActivityId}' (from fork {ForkInstanceId})")]
private partial void LogTokenRestored(Guid tokenId, string activityId, Guid forkInstanceId);

[LoggerMessage(EventId = 1050, Level = LogLevel.Information,
    Message = "Gateway fork state created: forkInstanceId={ForkInstanceId}, consumedToken={ConsumedTokenId}")]
private partial void LogGatewayForkStateCreated(Guid forkInstanceId, Guid? consumedTokenId);

[LoggerMessage(EventId = 1051, Level = LogLevel.Information,
    Message = "Gateway fork state removed: forkInstanceId={ForkInstanceId}")]
private partial void LogGatewayForkStateRemoved(Guid forkInstanceId);
```

**Step 2: Refactor CreateNextActivityEntry to add token logic**

The method signature needs the source entry's `ActivityInstanceId` for fork state tracking. Change the method to also accept the entry:

Replace the `CreateNextActivityEntry` method (lines 179-195) with:

```csharp
private async Task<ActivityInstanceEntry> CreateNextActivityEntry(
    Activity sourceActivity, IActivityInstanceGrain sourceInstance,
    Activity nextActivity, Guid? scopeId, Guid sourceActivityInstanceId)
{
    var sourceVariablesId = await sourceInstance.GetVariablesStateId();

    // Domain decides variable cloning behavior
    var variablesId = sourceActivity is Gateway { ClonesVariablesOnFork: true }
        ? State.AddCloneOfVariableState(sourceVariablesId)
        : sourceVariablesId;
    RequestContext.Set("VariablesId", variablesId.ToString());

    var newId = Guid.NewGuid();
    var newInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(newId);
    await newInstance.SetVariablesId(variablesId);
    await newInstance.SetActivity(nextActivity.ActivityId, nextActivity.GetType().Name);

    // Token propagation
    if (sourceActivity is Gateway { CreatesNewTokensOnFork: true })
    {
        var sourceTokenId = await sourceInstance.GetTokenId();
        var newTokenId = Guid.NewGuid();
        await newInstance.SetTokenId(newTokenId);

        var forkState = State.GatewayForks.FirstOrDefault(
            f => f.ForkInstanceId == sourceActivityInstanceId);
        if (forkState == null)
        {
            forkState = State.CreateGatewayFork(sourceActivityInstanceId, sourceTokenId);
            LogGatewayForkStateCreated(sourceActivityInstanceId, sourceTokenId);
        }
        forkState.CreatedTokenIds.Add(newTokenId);
        LogTokenCreated(sourceActivity.ActivityId, newTokenId);
    }
    else
    {
        // Inherit source's token
        var sourceTokenId = await sourceInstance.GetTokenId();
        if (sourceTokenId.HasValue)
        {
            await newInstance.SetTokenId(sourceTokenId.Value);
            LogTokenInherited(sourceTokenId.Value, nextActivity.ActivityId);
        }

        // Restore parent token after join completion
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
                LogTokenRestored(restoredToken.Value, nextActivity.ActivityId, forkState.ForkInstanceId);
                LogGatewayForkStateRemoved(forkState.ForkInstanceId);
            }
        }
    }

    return new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id, scopeId);
}
```

**Step 3: Update all callers of CreateNextActivityEntry to pass sourceActivityInstanceId**

There are 3 call sites in `WorkflowInstance.Execution.cs`. Each currently calls:

```csharp
await CreateNextActivityEntry(currentActivity, activityInstance, nextActivity, entry.ScopeId);
```

Update to also pass `entry.ActivityInstanceId`:

1. In `TransitionToNextActivity` (around line 239):
```csharp
var newEntry = await CreateNextActivityEntry(currentActivity, activityInstance, nextActivity, entry.ScopeId, entry.ActivityInstanceId);
```

2. In `CompleteFinishedSubProcessScopes` for subprocess (around line 302):
```csharp
var newEntry = await CreateNextActivityEntry(activity, activityInstance, nextActivity, entry.ScopeId, entry.ActivityInstanceId);
```

3. In `TryCompleteMultiInstanceHost` (around line 407):
```csharp
var newEntry = await CreateNextActivityEntry(mi, hostGrain, nextActivity, hostEntry.ScopeId, hostEntry.ActivityInstanceId);
```

**Step 4: Verify it builds and existing tests pass**

Run: `dotnet build src/Fleans/ && dotnet test src/Fleans/`
Expected: Build succeeded, all existing tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs \
  src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs
git commit -m "feat: add token propagation to CreateNextActivityEntry"
```

---

### Task 8: Application-Level Integration Tests — Basic Fork/Join

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/InclusiveGatewayTests.cs`

**Step 1: Write the core integration tests**

Follow the patterns from `ExclusiveGatewayTests.cs` and `ParallelGatewayTests.cs`.

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class InclusiveGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task InclusiveGateway_TwoOfThreeTrue_BothBranchesExecuteAndJoin()
    {
        // Arrange: start → fork(inclusive) → task1/task2/task3 → join(inclusive) → end
        // Conditions: task1=true, task2=true, task3=false
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — evaluate all conditions (no short-circuit)
        await workflowInstance.CompleteConditionSequence("fork", "seq_task1", true);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task2", true);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task3", false);

        // Both tasks should be active
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task1"));
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task2"));
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityId == "task3"));

        // Complete both tasks — join should complete and workflow finishes
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());
        await workflowInstance.CompleteActivity("task2", new ExpandoObject());

        snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task InclusiveGateway_OneOfThreeTrue_SingleBranchAndJoinProceeds()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — only one condition true
        await workflowInstance.CompleteConditionSequence("fork", "seq_task1", true);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task2", false);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task3", false);

        // Complete the single active task
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — join proceeds with just one token, workflow completes
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task InclusiveGateway_JoinShouldNotComplete_UntilAllActiveBranchesDone()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Two conditions true
        await workflowInstance.CompleteConditionSequence("fork", "seq_task1", true);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task2", true);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task3", false);

        // Complete only task1, task2 still pending
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — join should NOT be completed, workflow still running
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task2"));
    }

    [TestMethod]
    public async Task InclusiveGateway_AllConditionsFalse_TakesDefaultFlow()
    {
        // Arrange
        var workflow = CreateInclusiveWithDefaultFlow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // All conditions false
        await workflowInstance.CompleteConditionSequence("fork", "seq_task1", false);
        await workflowInstance.CompleteConditionSequence("fork", "seq_task2", false);

        // Assert — workflow completed via default path
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "endDefault");
    }

    [TestMethod]
    public async Task InclusiveGateway_NoShortCircuit_WaitsForAllConditions()
    {
        // Arrange
        var workflow = CreateInclusiveThreeBranchWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition true, but gateway should NOT auto-complete
        await workflowInstance.CompleteConditionSequence("fork", "seq_task1", true);

        // Assert — fork is still active (waiting for remaining conditions)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        // task1 should NOT be active yet — fork hasn't completed
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Fork should not transition until all conditions evaluated");
    }

    // --- Helper methods ---

    private static WorkflowDefinition CreateInclusiveThreeBranchWorkflow()
    {
        // start → fork(inclusive) → task1/task2/task3 → join(inclusive) → end
        var start = new StartEvent("start");
        var fork = new InclusiveGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var task3 = new TaskActivity("task3");
        var join = new InclusiveGateway("join", IsFork: false);
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "inclusive-test",
            Activities = [start, fork, task1, task2, task3, join, end],
            SequenceFlows =
            [
                new SequenceFlow("seq0", start, fork),
                new ConditionalSequenceFlow("seq_task1", fork, task1, "true"),
                new ConditionalSequenceFlow("seq_task2", fork, task2, "true"),
                new ConditionalSequenceFlow("seq_task3", fork, task3, "true"),
                new SequenceFlow("seq_join1", task1, join),
                new SequenceFlow("seq_join2", task2, join),
                new SequenceFlow("seq_join3", task3, join),
                new SequenceFlow("seq_end", join, end)
            ]
        };
    }

    private static WorkflowDefinition CreateInclusiveWithDefaultFlow()
    {
        // start → fork(inclusive) → task1/task2 or endDefault → end
        var start = new StartEvent("start");
        var fork = new InclusiveGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var endDefault = new EndEvent("endDefault");

        return new WorkflowDefinition
        {
            WorkflowId = "inclusive-default-test",
            Activities = [start, fork, task1, task2, endDefault],
            SequenceFlows =
            [
                new SequenceFlow("seq0", start, fork),
                new ConditionalSequenceFlow("seq_task1", fork, task1, "true"),
                new ConditionalSequenceFlow("seq_task2", fork, task2, "true"),
                new DefaultSequenceFlow("seq_default", fork, endDefault)
            ]
        };
    }
}
```

**Step 2: Run the tests**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "InclusiveGateway"`
Expected: All 5 tests pass

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/InclusiveGatewayTests.cs
git commit -m "test: add application-level integration tests for inclusive gateway"
```

---

### Task 9: Nested Inclusive Gateway Test

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/InclusiveGatewayTests.cs`

**Step 1: Add nested gateway test**

Add to `InclusiveGatewayTests.cs`:

```csharp
[TestMethod]
public async Task InclusiveGateway_Nested_InnerForkJoinInsideOuterBranch()
{
    // Arrange: start → outerFork → [branchA: innerFork → t1/t2 → innerJoin → taskA] / [branchB: taskB] → outerJoin → end
    var start = new StartEvent("start");
    var outerFork = new InclusiveGateway("outerFork", IsFork: true);
    var innerFork = new InclusiveGateway("innerFork", IsFork: true);
    var t1 = new TaskActivity("t1");
    var t2 = new TaskActivity("t2");
    var innerJoin = new InclusiveGateway("innerJoin", IsFork: false);
    var taskA = new TaskActivity("taskA");
    var taskB = new TaskActivity("taskB");
    var outerJoin = new InclusiveGateway("outerJoin", IsFork: false);
    var end = new EndEvent("end");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "nested-inclusive-test",
        Activities = [start, outerFork, innerFork, t1, t2, innerJoin, taskA, taskB, outerJoin, end],
        SequenceFlows =
        [
            new SequenceFlow("s0", start, outerFork),
            new ConditionalSequenceFlow("s_branchA", outerFork, innerFork, "true"),
            new ConditionalSequenceFlow("s_branchB", outerFork, taskB, "true"),
            new ConditionalSequenceFlow("s_t1", innerFork, t1, "true"),
            new ConditionalSequenceFlow("s_t2", innerFork, t2, "true"),
            new SequenceFlow("s_ij1", t1, innerJoin),
            new SequenceFlow("s_ij2", t2, innerJoin),
            new SequenceFlow("s_taskA", innerJoin, taskA),
            new SequenceFlow("s_oj1", taskA, outerJoin),
            new SequenceFlow("s_oj2", taskB, outerJoin),
            new SequenceFlow("s_end", outerJoin, end)
        ]
    };

    var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
    await workflowInstance.SetWorkflow(workflow);
    await workflowInstance.StartWorkflow();

    // Outer fork: both branches active
    await workflowInstance.CompleteConditionSequence("outerFork", "s_branchA", true);
    await workflowInstance.CompleteConditionSequence("outerFork", "s_branchB", true);

    // Inner fork: both sub-branches active
    await workflowInstance.CompleteConditionSequence("innerFork", "s_t1", true);
    await workflowInstance.CompleteConditionSequence("innerFork", "s_t2", true);

    // Complete inner tasks
    await workflowInstance.CompleteActivity("t1", new ExpandoObject());
    await workflowInstance.CompleteActivity("t2", new ExpandoObject());

    // Inner join should complete, taskA becomes active
    // Complete taskA and taskB
    await workflowInstance.CompleteActivity("taskA", new ExpandoObject());
    await workflowInstance.CompleteActivity("taskB", new ExpandoObject());

    // Outer join should complete, workflow finishes
    var instanceId = workflowInstance.GetPrimaryKey();
    var snapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsTrue(snapshot!.IsCompleted);
    CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
}
```

**Step 2: Run the test**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "Nested"`
Expected: PASS (token restoration ensures outer join collects the right tokens)

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/InclusiveGatewayTests.cs
git commit -m "test: add nested inclusive gateway integration test"
```

---

### Task 10: BPMN Parsing

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs` (after line 256, the parallelGateway block)
- Create: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/InclusiveGatewayParsingTests.cs`

**Step 1: Write parsing tests**

Follow the pattern from `GatewayTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Infrastructure.Bpmn;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class InclusiveGatewayParsingTests
{
    [TestMethod]
    public async Task ShouldParseInclusiveGateway_AsFork()
    {
        var bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="test" isExecutable="true">
                <startEvent id="start" />
                <inclusiveGateway id="ig1" />
                <endEvent id="end1" />
                <endEvent id="end2" />
                <sequenceFlow id="s1" sourceRef="start" targetRef="ig1" />
                <sequenceFlow id="s2" sourceRef="ig1" targetRef="end1">
                  <conditionExpression>true</conditionExpression>
                </sequenceFlow>
                <sequenceFlow id="s3" sourceRef="ig1" targetRef="end2">
                  <conditionExpression>true</conditionExpression>
                </sequenceFlow>
              </process>
            </definitions>
            """;

        var converter = new BpmnConverter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var gateway = workflow.Activities.OfType<InclusiveGateway>().SingleOrDefault();
        Assert.IsNotNull(gateway);
        Assert.AreEqual("ig1", gateway.ActivityId);
        Assert.IsTrue(gateway.IsFork, "More outgoing than incoming → should be fork");
    }

    [TestMethod]
    public async Task ShouldParseInclusiveGateway_AsJoin()
    {
        var bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="test" isExecutable="true">
                <scriptTask id="t1" scriptFormat="csharp"><script>_context.x = 1</script></scriptTask>
                <scriptTask id="t2" scriptFormat="csharp"><script>_context.x = 2</script></scriptTask>
                <inclusiveGateway id="ig1" />
                <endEvent id="end" />
                <sequenceFlow id="s1" sourceRef="t1" targetRef="ig1" />
                <sequenceFlow id="s2" sourceRef="t2" targetRef="ig1" />
                <sequenceFlow id="s3" sourceRef="ig1" targetRef="end" />
              </process>
            </definitions>
            """;

        var converter = new BpmnConverter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var gateway = workflow.Activities.OfType<InclusiveGateway>().SingleOrDefault();
        Assert.IsNotNull(gateway);
        Assert.IsFalse(gateway.IsFork, "More incoming than outgoing → should be join");
    }

    [TestMethod]
    public async Task ShouldParseInclusiveGateway_DefaultAttribute()
    {
        var bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="test" isExecutable="true">
                <startEvent id="start" />
                <inclusiveGateway id="ig1" default="s3" />
                <endEvent id="end1" />
                <endEvent id="end2" />
                <sequenceFlow id="s1" sourceRef="start" targetRef="ig1" />
                <sequenceFlow id="s2" sourceRef="ig1" targetRef="end1">
                  <conditionExpression>true</conditionExpression>
                </sequenceFlow>
                <sequenceFlow id="s3" sourceRef="ig1" targetRef="end2" />
              </process>
            </definitions>
            """;

        var converter = new BpmnConverter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        // The default flow should be parsed as DefaultSequenceFlow
        var defaultFlow = workflow.SequenceFlows.OfType<Domain.Sequences.DefaultSequenceFlow>().SingleOrDefault();
        Assert.IsNotNull(defaultFlow);
        Assert.AreEqual("s3", defaultFlow.SequenceFlowId);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "InclusiveGateway"`
Expected: FAIL — BpmnConverter doesn't parse `<inclusiveGateway>` yet

**Step 3: Add inclusiveGateway parsing to BpmnConverter**

In `BpmnConverter.cs`, after the `parallelGateway` parsing block (after line 256), add:

```csharp
// Parse inclusive gateways
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
    if (outgoingCount > incomingCount)
    {
        isFork = true;
    }
    else if (incomingCount > outgoingCount)
    {
        isFork = false;
    }
    else if (incomingCount <= 1)
    {
        isFork = true;
    }
    else
    {
        throw new InvalidOperationException(
            $"Inclusive gateway '{id}' has {incomingCount} incoming and {outgoingCount} outgoing flows. " +
            "Mixed inclusive gateways (both join and fork) are not supported. " +
            "Split into separate join and fork gateways.");
    }

    var activity = new InclusiveGateway(id, IsFork: isFork);
    activities.Add(activity);
    activityMap[id] = activity;
}
```

**Step 4: Run parsing tests**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "InclusiveGateway"`
Expected: All 3 tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs \
  src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/InclusiveGatewayParsingTests.cs
git commit -m "feat: parse <inclusiveGateway> BPMN elements with fork/join detection"
```

---

### Task 11: GatewayForkState Cleanup

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs` (the `Complete()` method at line 91-98)

**Step 1: Add cleanup on workflow completion**

In `WorkflowInstanceState.Complete()` (line 97), before `IsCompleted = true;`, add:

```csharp
GatewayForks.Clear();
```

**Step 2: Verify it builds and all tests pass**

Run: `dotnet build src/Fleans/ && dotnet test src/Fleans/`
Expected: Build succeeded, all tests pass

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs
git commit -m "fix: clear GatewayForks on workflow completion"
```

---

### Task 12: Update README + Audit Checklist

**Files:**
- Modify: `README.md` (line 47 — Inclusive Gateway row)
- Modify: `docs/plans/2026-02-17-architectural-risk-audit.md` (items 2.4, 2.5, 3.1, 3.2)

**Step 1: Mark Inclusive Gateway as implemented in README**

In `README.md` line 47, change:

```
| Inclusive Gateway    | Indicates a decision point where one or more paths can be taken.             |             |
```

to:

```
| Inclusive Gateway    | Indicates a decision point where one or more paths can be taken.             |    [x]      |
```

**Step 2: Update the audit checklist**

In `2026-02-17-architectural-risk-audit.md`:

Mark items 2.4 and 2.5 as done (multi-instance was already implemented):

```
- [x] **2.4 — Multi-Instance Activity (parallel)**
- [x] **2.5 — Multi-Instance Activity (sequential)**
```

Mark items 3.1 and 3.2 as done:

```
- [x] **3.1 — Token propagation**
- [x] **3.2 — Inclusive Gateway**
```

**Step 3: Commit**

```bash
git add README.md docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: mark Inclusive Gateway as implemented in README and audit"
```

---

### Task 13: Manual Test Fixtures

**Files:**
- Create: `tests/manual/14-inclusive-gateway/test-plan.md`
- Create: `tests/manual/14-inclusive-gateway/parallel-conditions.bpmn`
- Create: `tests/manual/14-inclusive-gateway/default-flow.bpmn`

**Step 1: Create test plan**

Create `tests/manual/14-inclusive-gateway/test-plan.md`:

```markdown
# Inclusive Gateway Manual Test Plan

## Scenario 1: Parallel Conditions (parallel-conditions.bpmn)

**Prerequisites:** Aspire running (`dotnet run --project Fleans.Aspire`)

**Steps:**
1. Deploy `parallel-conditions.bpmn` via Web UI
2. Start a workflow instance
3. Observe: the inclusive fork evaluates 3 conditions
4. Conditions 1 and 2 return true, condition 3 returns false
5. Verify: tasks on branches 1 and 2 execute
6. Complete both tasks
7. Verify: inclusive join proceeds, workflow completes

**Expected:**
- [ ] Fork waits for all conditions before transitioning
- [ ] Only true-condition branches are activated
- [ ] Join waits for all active branches
- [ ] Workflow completes after join

## Scenario 2: Default Flow (default-flow.bpmn)

**Steps:**
1. Deploy `default-flow.bpmn` via Web UI
2. Start a workflow instance
3. Observe: all conditions evaluate to false
4. Verify: default path is taken
5. Verify: workflow completes via default end event

**Expected:**
- [ ] All conditions evaluated (no short-circuit)
- [ ] Default flow taken when all false
- [ ] Workflow completes via default path
```

**Step 2: Create BPMN fixtures**

Create `tests/manual/14-inclusive-gateway/parallel-conditions.bpmn` with a simple inclusive fork/join flow. The implementer should use a BPMN editor or write the XML following the BPMN fixture authoring rules from `CLAUDE.md`:
- Use `<scriptTask>` with `scriptFormat="csharp"` (not bare `<task>`)
- Include `<bpmndi:BPMNDiagram>` section
- Use `<inclusiveGateway>` elements for fork and join

**Step 3: Commit**

```bash
git add tests/manual/14-inclusive-gateway/
git commit -m "docs: add manual test plan and BPMN fixtures for inclusive gateway"
```

---

### Task 14: Final Verification

**Step 1: Run the full test suite**

Run: `dotnet test src/Fleans/`
Expected: All tests pass (existing + new inclusive gateway tests)

**Step 2: Build the full solution**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded, no warnings related to new code

**Step 3: Verify Aspire startup (optional, manual)**

Run: `dotnet run --project src/Fleans/Fleans.Aspire/`
Expected: Web UI loads, no startup errors

**Step 4: Final commit if any cleanup needed**

If any small fixes were needed during verification, commit them.
