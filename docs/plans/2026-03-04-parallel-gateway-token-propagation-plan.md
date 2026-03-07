# ParallelGateway Token Propagation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Migrate ParallelGateway from static graph-analysis join detection to token-based join detection by extracting a shared `ForkJoinGateway` base class used by both ParallelGateway and InclusiveGateway.

**Architecture:** Extract `AllExpectedTokensArrived()` from InclusiveGateway into a new abstract `ForkJoinGateway : ConditionalGateway` base class. Both ParallelGateway and InclusiveGateway inherit from it. ParallelGateway fork transitions gain `TokenAction.CreateNew`; join transitions gain `TokenAction.RestoreParent`. The old `AllIncomingPathsCompleted()` is deleted.

**Tech Stack:** C# 14, .NET 10, Orleans 9.2.1, MSTest, Orleans.TestingHost

**Design doc:** `docs/plans/2026-03-04-parallel-gateway-token-propagation-design.md`

---

### Task 1: Create ForkJoinGateway base class

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/ForkJoinGateway.cs`

**Step 1: Create the `ForkJoinGateway` abstract record**

Create `src/Fleans/Fleans.Domain/Activities/ForkJoinGateway.cs`:

```csharp
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record ForkJoinGateway(
    string ActivityId,
    bool IsFork) : ConditionalGateway(ActivityId)
{
    internal override bool IsJoinGateway => !IsFork;

    protected async Task<bool> AllExpectedTokensArrived(
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

This is a straight extraction of `AllExpectedTokensArrived` from `InclusiveGateway.cs:121-149`, changed from `private` to `protected`. The `IsFork` property and `IsJoinGateway` override are shared by both ParallelGateway and InclusiveGateway.

**Step 2: Build to verify compilation**

Run: `dotnet build src/Fleans/Fleans.Domain/Fleans.Domain.csproj`
Expected: Build succeeded. The new class is not used yet so nothing can break.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/ForkJoinGateway.cs
git commit -m "feat: add ForkJoinGateway base class with token-based join detection"
```

---

### Task 2: Migrate InclusiveGateway to extend ForkJoinGateway

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/InclusiveGateway.cs`

**Step 1: Change InclusiveGateway to extend ForkJoinGateway and remove duplicated code**

In `src/Fleans/Fleans.Domain/Activities/InclusiveGateway.cs`:

1. Change the record declaration from `ConditionalGateway(ActivityId)` to `ForkJoinGateway(ActivityId, IsFork)`.
2. Remove the `[property: Id(1)] bool IsFork` parameter (now inherited from `ForkJoinGateway`).
3. Remove the `IsJoinGateway` override (now inherited from `ForkJoinGateway`).
4. Remove the `AllExpectedTokensArrived` method (now inherited from `ForkJoinGateway`).
5. Keep `SetConditionResult`, `ExecuteAsync`, and `GetNextActivities` — they contain InclusiveGateway-specific logic.

The result should be:

```csharp
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record InclusiveGateway(
    string ActivityId,
    bool IsFork
) : ForkJoinGateway(ActivityId, IsFork)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        if (IsFork)
        {
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

    internal override async Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        if (!IsFork)
        {
            return definition.SequenceFlows.Where(sf => sf.Source == this)
                .Select(flow => new ActivityTransition(flow.Target, Token: TokenAction.RestoreParent))
                .ToList();
        }

        var sequencesState = await workflowContext.GetConditionSequenceStates();
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        if (!sequencesState.TryGetValue(activityInstanceId, out var activitySequencesState))
            activitySequencesState = [];

        var trueTargets = activitySequencesState
            .Where(x => x.Result)
            .Select(x => definition.SequenceFlows
                .First(sf => sf.SequenceFlowId == x.ConditionalSequenceFlowId).Target)
            .Select(target => new ActivityTransition(target, CloneVariables: true, Token: TokenAction.CreateNew))
            .ToList();

        if (trueTargets.Count > 0)
            return trueTargets;

        var defaultFlow = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

        if (defaultFlow is not null)
            return [new ActivityTransition(defaultFlow.Target, CloneVariables: true, Token: TokenAction.CreateNew)];

        throw new InvalidOperationException(
            $"InclusiveGateway {ActivityId}: no true condition and no default flow");
    }
}
```

**Step 2: Build the domain project**

Run: `dotnet build src/Fleans/Fleans.Domain/Fleans.Domain.csproj`
Expected: Build succeeded.

**Step 3: Run the InclusiveGateway tests to confirm no regressions**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~InclusiveGateway" -v n`
Expected: All 5 tests pass (TwoOfThreeTrue, OneOfThreeTrue, JoinShouldNotComplete, AllConditionsFalse_TakesDefaultFlow, Nested).

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/InclusiveGateway.cs
git commit -m "refactor: migrate InclusiveGateway to extend ForkJoinGateway"
```

---

### Task 3: Migrate ParallelGateway to extend ForkJoinGateway with token propagation

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/ParallelGateway.cs`

**Step 1: Rewrite ParallelGateway to extend ForkJoinGateway**

Replace the entire contents of `src/Fleans/Fleans.Domain/Activities/ParallelGateway.cs` with:

```csharp
using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ParallelGateway(
    string ActivityId,
    [property: Id(1)] bool IsFork) : ForkJoinGateway(ActivityId, IsFork)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        if (IsFork)
        {
            await activityContext.Complete();
        }
        else
        {
            if (await AllExpectedTokensArrived(workflowContext, definition))
            {
                await activityContext.Complete();
            }
        }

        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        if (!IsFork)
        {
            // Join: restore parent token
            var joinFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
                .Select(flow => new ActivityTransition(flow.Target, Token: TokenAction.RestoreParent))
                .ToList();
            return Task.FromResult(joinFlows);
        }

        // Fork: all outgoing paths with cloned variables and new tokens
        var nextFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
            .Select(flow => new ActivityTransition(flow.Target, CloneVariables: true, Token: TokenAction.CreateNew))
            .ToList();

        return Task.FromResult(nextFlows);
    }
}
```

Key changes from the original:
1. Base class: `Gateway(ActivityId)` → `ForkJoinGateway(ActivityId, IsFork)`
2. Join: `AllIncomingPathsCompleted()` → `AllExpectedTokensArrived()` (inherited from ForkJoinGateway)
3. Fork transitions: `new ActivityTransition(flow.Target, CloneVariables: IsFork)` → `new ActivityTransition(flow.Target, CloneVariables: true, Token: TokenAction.CreateNew)`
4. Join transitions: `new ActivityTransition(flow.Target, CloneVariables: false)` → `new ActivityTransition(flow.Target, Token: TokenAction.RestoreParent)`
5. Removed `IsJoinGateway` override (inherited from ForkJoinGateway)
6. Removed `AllIncomingPathsCompleted()` method (replaced by token-based join)

**Step 2: Build the solution**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded. The `[property: Id(1)] bool IsFork` is kept on ParallelGateway's constructor because it was already there for serialization — ForkJoinGateway doesn't have `[property: Id(1)]` on its `IsFork` since it's abstract and not directly serialized.

**Step 3: Run ALL tests to verify no regressions**

Run: `dotnet test src/Fleans/ -v n`
Expected: All tests pass. The ParallelGateway tests exercise the same fork/join behavior which is now token-based but behaviorally equivalent.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/ParallelGateway.cs
git commit -m "refactor: migrate ParallelGateway to token-based join via ForkJoinGateway"
```

---

### Task 4: Update the architectural risk audit document

**Files:**
- Modify: `docs/plans/2026-02-17-architectural-risk-audit.md`

**Step 1: Update the audit document**

In `docs/plans/2026-02-17-architectural-risk-audit.md`, update the Phase 3 section to note that ParallelGateway now also uses token propagation. Find the Phase 3 checklist items and add a note:

After item 3.2 (Inclusive Gateway), add:

```markdown
- [x] **3.3 — ParallelGateway token migration**: ParallelGateway migrated from static graph-analysis join (`AllIncomingPathsCompleted`) to token-based join (`AllExpectedTokensArrived`) via shared `ForkJoinGateway` base class. *Done.*
```

**Step 2: Commit**

```bash
git add docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: update arch audit — ParallelGateway migrated to token propagation"
```
