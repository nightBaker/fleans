---
title: Variables and Scope
description: How workflow variables, the script-task _context, and scope inheritance work in Fleans — with worked examples and merge semantics.
sidebar:
  order: 8
---


Workflow variables are the data that flows through a Fleans workflow. They carry inputs from `POST /Workflow/start`, are mutated by script tasks and service tasks, get read by gateway conditions and output mappings, and end up persisted alongside the workflow's event stream.

This guide covers the mental model, how to read and write variables from script tasks, how scopes nest and inherit, and — most importantly — when a child scope's variables flow back to the parent.

## The variable model

Each workflow instance starts with a single **root variable scope**. As the workflow executes, the engine opens new **child scopes** for embedded SubProcesses, event sub-processes (ESPs), multi-instance loop bodies, parallel-fork branches, and compensation handlers. Scopes are organised as a tree rooted at the workflow.

Inside each scope, variables live in an `ExpandoObject` — a dynamic dictionary-like container. The container is mutated **in place** by script tasks and serialized via `Newtonsoft.Json` for persistence.

Types that round-trip cleanly:

- Numbers (int, long, double, decimal)
- Strings
- Booleans
- Nested objects (you can build a tree by assigning one ExpandoObject's field to another)
- Arrays / lists of the above

A field that has never been assigned is `null` when accessed dynamically — there is no "missing" sentinel distinct from null at the scripting layer.

## Reading variables in script tasks

Inside a `<scriptTask scriptFormat="csharp">`, the engine binds the active scope's `ExpandoObject` to the variable name `_context` before evaluating each script statement. (See `Fleans.Infrastructure/Scripts/DynamicExpressoScriptExpressionExecutor.cs` line 46 — `interpreter.SetVariable("_context", variables);`.)

You read variables as fields of `_context`:

```csharp
// Top-level field
var name = (string)_context.userName;

// Nested field
var city = (string)_context.user.address.city;

// Default-when-null pattern
var attempts = _context.retryCount ?? 0;
```

DynamicExpresso evaluates each statement independently. You cannot define helper methods inside a script task — keep logic linear, and put complex computation behind a custom service-task plugin (see [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/)).

## Writing variables in script tasks

Assignment writes the field on the **active scope's** `ExpandoObject`:

```csharp
_context.greeting = "hello, " + _context.userName;
_context.attempts = (_context.attempts ?? 0) + 1;
```

Persistence is event-sourced: at the end of the activity, the engine takes a snapshot of the mutated scope and emits the appropriate domain event. If a script task **fails** (throws), the partial mutations are not committed — the activity transitions to its error path with the variables it had on entry.

## Scope inheritance

Reads walk **up the chain**. When a script task in a child scope reads `_context.foo`, the engine resolves `foo` by checking the active scope first, then its parent, then its grandparent, up to the root.

Writes are **scope-local**. `_context.foo = ...` creates-or-updates `foo` on the active scope only. It does not mutate any ancestor scope. The only way a child scope's variables reach an ancestor is via an explicit merge event — see [Merge semantics](#merge-semantics) below.

This is the source of the common pitfall *"my SubProcess wrote a variable but the parent doesn't see it"* — see [Common pitfalls](#common-pitfalls).

## Scope creation

Fleans defines four variable-scope domain events in `Fleans.Domain/Events/WorkflowDomainEvents.cs` (lines 26-29):

- `ChildVariableScopeCreated(ScopeId, ParentScopeId)` — a new empty child scope opens that resolves reads via its parent
- `VariableScopeCloned(NewScopeId, SourceScopeId)` — a new scope opens **pre-populated** with a snapshot of the source scope's variables (no parent link required for resolution)
- `VariablesMerged(VariablesId, Variables)` — a set of fields is merged into an existing scope (last-write-wins on the merged keys)
- `VariableScopesRemoved(ScopeIds)` — listed scopes are deleted from the workflow's scope tree

These events are emitted from roughly 22 sites in `Fleans.Domain/Aggregates/WorkflowExecution.cs`, grouped into the following categories:

- **Embedded SubProcess entry** — opens a `ChildVariableScopeCreated` so the SubProcess body sees enclosing variables but its writes stay local until merge.
- **Event sub-process activation (interrupting + non-interrupting)** — handler activation creates a fresh child scope and (when the trigger carries data, e.g. a message payload) merges those fields into the new scope.
- **Multi-instance loop body** — each iteration is `VariableScopeCloned` from the host scope, optionally augmented with the per-iteration item value.
- **Parallel fork branches** — every outgoing branch from a parallel fork gets a `VariableScopeCloned` snapshot of the fork-time variables, giving each branch an isolated copy.
- **Compensation handler activation** — each handler gets a fresh child scope **seeded from the compensable activity's completion-time snapshot**, overlaying the enclosing scope.
- **Escalation handler activation** — host scope is cloned for the handler; if the escalation throw carries variables they are merged in.
- **Branch / scope cleanup** — once a join completes (or an interrupting boundary cancels siblings), the consumed child scopes are removed via `VariableScopesRemoved`.

The doc deliberately does not enumerate every emit site — read `WorkflowExecution.cs` if you need the exact code path.

## Merge semantics

This is the section everyone gets wrong. The rule is: **a child scope's variables do NOT reach an ancestor until an explicit `VariablesMerged` event targets the ancestor.** The cases below cover when that merge happens.

### Compensation handlers — MANDATORY merge

:::caution[Compensation handlers MUST merge]
> Compensation handlers run in isolated child scopes — each handler gets a fresh variable scope seeded with the compensable activity's completion-time snapshot, overlaying the enclosing scope. After a handler completes successfully, its variable changes MUST be merged back into the enclosing scope before the next handler spawns.

Source: `CLAUDE.md` (Design Constraints). Otherwise (a) later handlers in the walk see stale variables, and (b) compensation side-effects vanish after the walk finishes.
:::

`WorkflowExecution.AdvanceCompensationWalkIfHandlerCompleted` emits a `VariablesMerged` event with the handler's full variable map targeting the parent scope's variables ID (root scope's if the walk is at root).

### Parallel fork-join — merge AT JOIN, not as-you-go

Parallel branches run with **isolated** scopes (each branch gets a `VariableScopeCloned` snapshot at fork time). A branch writing `_context.shared = "A"` does not affect a sibling branch.

When all branches arrive at the join gateway, the engine merges each branch's variables into the original (pre-fork) scope **in token-creation order — last write wins** — and then emits `VariableScopesRemoved` for the now-consumed branch scopes. After the join, exactly one scope remains for the joined token.

This is the behaviour exercised by manual test plan #12 (`tests/manual/12-variable-scoping/test-plan.md`): the merged scope contains `shared` with the last branch's value, and both branches' branch-local variables end up in the merged scope.

### Embedded SubProcess completion

When an embedded SubProcess completes normally, its child scope's variables are merged back into the enclosing scope before the SubProcess scope is removed. Variables written inside the SubProcess **become visible** in the parent after completion.

### Event sub-processes (interrupting / non-interrupting)

Both flavours run in fresh child scopes seeded from the enclosing scope (and trigger payload, if any). On handler completion, the engine merges the handler's variables back into the enclosing scope and removes the handler scope — same shape as embedded SubProcess completion.

### Multi-instance loops

Each iteration's scope is cloned from the host. Per-iteration writes stay isolated. After all iterations complete, the multi-instance activity completes; if you need to aggregate results across iterations, write to a collection field on the host scope **before** the multi-instance activity (so each iteration appends to the cloned copy) or use the BPMN output collection mapping. Per-iteration scopes are removed at completion; their variables do not auto-merge en masse into the host.

## Common pitfalls

**"My script task wrote `_context.x = 5` but a downstream task sees null"**

The downstream task is probably running in a sibling scope or a parent scope, not a descendant. Writes are scope-local — only descendants and the same scope read your write. If you wrote inside a SubProcess and the reader is after the SubProcess, the write becomes visible only after the SubProcess merges (which happens at SubProcess completion).

**"Branch A wrote `_context.shared = 'A'` but Branch B sees the pre-fork value"**

This is correct, by design. Parallel-fork branches are isolated (`VariableScopeCloned` at fork). Cross-branch communication has to go through a join + the merged post-join scope, not through shared variables mid-fork. See manual test plan #12.

**"My script task threw, and the variable I wrote earlier in the same script disappeared"**

Correct. Script-task failure rolls back the in-flight scope mutations for that activity. Split work that must persist independently across multiple script tasks.

**"`ExpandoObject` vs `JObject` — why does my nested field surface as a `JObject`?"**

If a complex value enters the workflow via `POST /Workflow/start` `Variables`, it deserializes through `Newtonsoft.Json` and may surface as `JObject` / `JArray` rather than nested `ExpandoObject`. Cast or convert when reading deeply nested fields. Top-level fields work transparently.

## Worked examples

The canonical fixture for variable-scope behaviour is the manual test plan at `tests/manual/12-variable-scoping/test-plan.md` (`parallel-variable-isolation.bpmn`):

- A pre-fork script task sets `shared` to an initial value.
- A parallel fork creates two branches; each writes a different value to `shared`.
- The branches join. The merged scope contains a single `shared` field — the last branch's value (in token-creation order) — plus any branch-local fields each branch wrote, all consolidated into one scope.
- The two transient branch scopes are removed (`VariableScopesRemoved`). The instance completes with one scope.

For SubProcess merging, see manual test plan #7 (`tests/manual/07-subprocess/`). For compensation merging, see manual test plan #29 (`tests/manual/24-compensation-event/`).

## See also

- [BPMN Support](/fleans/concepts/bpmn-support/) — which BPMN elements open new scopes
- [API Reference](/fleans/reference/api/) — `POST /Workflow/start` initial variables, `POST /Workflow/complete-activity` output variables
- [Persistence](/fleans/reference/persistence/) — how variable state and scope-tree events are serialized
- [User Tasks](/fleans/guides/user-tasks/) — output variable mappings on user-task completion
