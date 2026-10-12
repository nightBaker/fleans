# Load-bearing invariants

These guard correctness. Break them and silent bugs ship. `CLAUDE.md` carries a one-line summary of each; this file is the full rule set — read it before touching activity lifecycle, compensation, effect handlers, user-task claims, or anything reachable from a stream handler.

## Each activity instance executes at most once

Every non-boundary activity instance runs exactly once (completes OR fails). An activity definition can be visited multiple times (e.g., in a loop), creating a new instance each time. `TimerCallbackGrain` keys on `hostActivityInstanceId` to distinguish.

## Compensation handlers run in isolated child scopes

Each handler gets a fresh variable scope seeded with the compensable activity's completion-time snapshot, overlaying the enclosing scope. After a handler completes successfully, its variable changes MUST be merged back into the enclosing scope before the next handler spawns. Otherwise: (a) later handlers see stale variables, and (b) compensation side-effects vanish after the walk finishes. Enforced via `WorkflowExecution.AdvanceCompensationWalkIfHandlerCompleted` emitting a `VariablesMerged` event targeting the parent scope's variables ID.

## Registration-vs-cleanup error asymmetry

Registration-path failures (build-time `ProcessRegisterMessage`, handler-time `RegisterTimerEffect.Activate`, `SubscribeMessageEffect` / `SubscribeSignalEffect`) MUST route to `FailActivity` so the workflow surfaces the failure.

Cleanup-path failures (`UnsubscribeMessageEffect`, `UnsubscribeSignalEffect`, `UnregisterTimerEffect`, `ThrowSignalEffect`) MUST be log-only — the owning activity has already completed or been cancelled, and failing now would violate the "at most once" invariant.

Effect handlers in `Fleans.Application/Effects/{Message,Signal,Timer}EffectHandler.cs` enforce this.

## User-task claim authorization is a three-rule OR

`UserTaskLifecycle.Claim` allows a claim iff at least one of:

- (a) `Assignee == userId`
- (b) `CandidateUsers.Contains(userId)`
- (c) `CandidateGroups ∩ userGroups ≠ ∅` (ordinal case-sensitive)

When no constraint is present the task is unrestricted.

Caller-side `userGroups` comes from `IUserGroupResolver` — `JwtUserGroupResolver` reads `HttpContext.User.FindAll("groups")` when `Authentication:Authority` is configured; `BodyUserGroupResolver` reads the body when auth is disabled (body-supplied groups are ignored under JWT so callers can't spoof). The acting `userId` follows the same split via `IUserIdResolver` (#793): under JWT it comes from the `Authentication:UserIdClaim` claim and a differing body `UserId` is a 403.

Rejection messages MUST stay identifier-free; structured audit lives in `LogUserTaskClaimRejected` (EventId 1066, Warning) carrying `UserGroupCount` only — never group names.

**When extending (e.g., role-based claims): add a 4th branch, keeping the existing three.**

## Stream-redelivery idempotency

Every state-mutating grain method reachable from an `[ImplicitStreamSubscription]` handler MUST be idempotent under at-least-once delivery. Silent doubling under redelivery is a production correctness bug, not a test flake.

Current guarantees are a layered guard set:

1. Grain-entry stale guard `HasActiveEntry(activityInstanceId)` on `CompleteActivity`/`FailActivity`.
2. Domain-entry stale guard `entry.IsCompleted` as defense-in-depth.
3. `joinState.HasFired` on `CompleteActivationCondition`.
4. `entry.IsCompleted || entry.ActivityInstanceId != hostActivityInstanceId` on `CompleteMultiInstanceEarly`.
5. `CompleteConditionSequence` relies on set-by-key projection idempotency.
6. `WorkflowExecution.IsTimerFireStale(timerActivityId, hostActivityInstanceId)` pre-call check on `HandleTimerFired` — mirrors the aggregate's internal regular-timer and ESP-scope-container guards; late reminder fires (post-completion, post-cancel) are logged as warnings (EventId 3034) and silently dropped. Pairs with `TimerCallbackGrain.Cancel`'s swallow-on-failure cleanup per the registration-vs-cleanup asymmetry rule.
7. **Pending-external-event op-id ledger** (`State.PendingOperations` / `State.AppliedOperations`, #657) — the three caller-retried sibling-grain RPCs (`OnChildWorkflowCompleted`/`OnChildWorkflowFailed`/`OnChildEscalationRaised`) take a deterministic op-id and dedup on it: applied → return persisted outcome; in-flight → await existing in-memory TCS; fresh → durably enqueue via `RaiseEvent(PendingEventEnqueued)`. The queue is persisted (survives forced deactivation; rebuilt with fresh TCSes in `OnActivateAsync`), and the drain path emits `PendingEventDrained` (+ `PendingEventApplied` for retried ops). `AppliedOperations` is GC'd on terminal state (guard 2's `IsCompleted` covers post-completion retries). The signal path is never retried, so it persists with a fresh-Guid key and no dedup. Caller side: `IGrainFactory.CallWithRetry`; the op-id *string* format lives in the application layer (the domain effect carries only a raw `EscalationInstanceId` Guid).
8. **Pre-execution activity check** (a read, not a lock) `IWorkflowInstanceCallback.GetVariablesIfActive(activityInstanceId, variablesId)` in `CustomTaskHandlerBase.OnNextAsync` — runs *before* the plugin's `ExecuteAsync` and returns `null` once the activity instance has completed or failed, so a redelivered/duplicate `ExecuteCustomTaskEvent` is logged (EventId 4038) and skipped instead of re-running plugin side effects. Guards 1–2 only absorb the duplicate *completion*; this one prevents the duplicate *execution* of a finished activity. Error split in the handler: transient grain-call failures (`TimeoutException`, `OrleansException`) of the check or of `CompleteActivity` are rethrown for stream redelivery (EventId 4039) — never `FailActivity`, since the plugin either has not run or already succeeded; any other exception (deterministic, e.g. unknown variable scope) goes to `FailActivity`. **Scope: custom tasks only** — other stream handlers (script, condition evaluation) still re-execute on redelivery and rely on guards 1–5 to drop the duplicate result. **Not covered** (the plugin runs again; plugins need an idempotency key): a run interrupted mid-`ExecuteAsync` (worker crash, activation deactivation), a transient `CompleteActivity` failure after the plugin succeeded, and two concurrent handler activations during a membership change (both read "active" before either completes — closing that needs a durable claim on the workflow instance, #818). Regression tests: `CustomTaskRedeliveryTests` (redelivery after completion and after failure, deterministic check error fails the activity, multi-silo test with queued duplicates).

**When adding a new state-mutating method on `IWorkflowInstanceCallback` (or any other interface called from a stream handler), extend this guard set or prove the new path is naturally idempotent.**
