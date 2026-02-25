# Architectural Risk Audit: Unimplemented BPMN Elements

**Date:** 2026-02-17
**Purpose:** Identify which unimplemented BPMN elements would stress or break the current architecture, grouped by the core assumption they violate.

## Current Architectural Assumptions

| ID | Assumption | Description |
|---|---|---|
| A1 | Synchronous execution | `WorkflowInstance.ExecuteWorkflow()` runs a `while(AnyNotExecuting)` loop. Activities either complete immediately or wait for an external `CompleteActivity()` call. No concept of "wait" or "sleep." |
| A2 | Flat activities | All activities live in a single flat list in `WorkflowDefinition.Activities`. No nesting or containment. CallActivity achieves isolation via a separate `WorkflowInstance` grain. |
| A3 | Binary condition evaluation | ExclusiveGateway takes exactly 1 path. ParallelGateway takes all paths. Join side knows expected count statically from the definition. |
| A4 | Interrupting-only error handling | `BoundaryErrorEvent` always replaces the attached activity. No non-interrupting boundary events. No compensation. |
| A5 | Flat variable state | `WorkflowInstanceState.VariableStates` is a flat list of `ExpandoObject` entries. No variable scoping beyond CallActivity input/output mappings. |

---

## A1: Synchronous Execution (no suspend/resume)

**What breaks it:**

| BPMN Element | Why | Severity |
|---|---|---|
| Timer Event (boundary, intermediate catch, start) | Workflow must pause for a duration/date, then resume automatically. No external `CompleteActivity()` call — the engine must schedule the wake-up. | HIGH |
| Message Event (intermediate catch, boundary, start) | Workflow pauses until a specific correlated message arrives. Requires a message registry + correlation mechanism. | HIGH |
| Signal Event (intermediate catch/throw, boundary, start) | Like Message but broadcast — one signal wakes many workflows. Requires pub/sub across workflow instances. | HIGH |
| Event-Based Gateway | Decision depends on which event fires first (timer vs message vs signal). Must register for multiple events, race them, cancel losers. | VERY HIGH |

**Architectural impact:**
- Suspend/resume model — workflows must be able to pause mid-execution and resume later
- Scheduling mechanism — Orleans Reminders fit naturally for timer-based wake-ups
- Message correlation registry — new infrastructure to route incoming messages to the right workflow
- Signal broadcast mechanism — Orleans Streams fit naturally for one-to-many delivery

**Key insight:** Timer Events alone force the suspend/resume infrastructure, which then unblocks Message, Signal, and Event-Based Gateway at lower incremental cost.

---

## A2: Flat Activities (no nesting/scoping)

**What breaks it:**

| BPMN Element | Why | Severity |
|---|---|---|
| Embedded Sub-Process | Container holding child activities with its own scope. Boundary events attach to the sub-process. | HIGH |
| Transaction Sub-Process | Embedded sub-process + compensation/cancel/hazard handlers. Failed transactions must compensate child activities in reverse. | VERY HIGH |
| Ad-Hoc Sub-Process | Activities inside have no sequence flows — execute in any order based on external triggers. | VERY HIGH |
| Multi-Instance Activity (Loop) | Single activity definition spawns N parallel or sequential instances. Dynamic grain creation and result aggregation. | HIGH |

**Architectural impact:**
- `WorkflowDefinition` needs a tree structure — activities must contain child activities
- `WorkflowInstanceState` needs scoped variable stacks (coupled with A5)
- Boundary events must work on sub-process containers — firing cancels all child activities
- Execution loop must recurse into sub-process scopes

**Key insight:** Embedded Sub-Process is the gateway element. Transaction and Ad-Hoc build on it. Tightly coupled with A5.

---

## A3: Binary Condition Evaluation (exclusive=1, parallel=all)

**What breaks it:**

| BPMN Element | Why | Severity |
|---|---|---|
| Inclusive Gateway | Fork: evaluate all conditions, take 1..N matching paths. Join: must know dynamically how many branches were taken. | HIGH |
| Complex Gateway | Arbitrary activation expressions. Spec is intentionally vague. | MEDIUM |

**Architectural impact:**
- Fork side is manageable — evaluate all conditions, activate matching flows
- Join side requires token propagation — each branch carries a token, join counts arrived tokens to know when to proceed
- New `TokenId` or branch tracking concept needed on `ActivityInstanceEntry`

**Key insight:** The fork side is a variation of ExclusiveGateway. The join side requires runtime token counting, which is a fundamentally new concept. Complex Gateway can be deferred.

---

## A4: Interrupting-Only Error Handling

**What breaks it:**

| BPMN Element | Why | Severity |
|---|---|---|
| Escalation Event (non-interrupting boundary) | Attached activity continues running while escalation handler runs in parallel. | MEDIUM |
| Non-interrupting Boundary Timer | Activity keeps running; timer spawns a parallel branch. | MEDIUM |
| Non-interrupting Boundary Message/Signal | Activity continues; handler runs alongside. | MEDIUM |
| Compensation Event | Completed activities' compensation handlers invoked in reverse order to "undo" work. | HIGH |

**Architectural impact:**
- Non-interrupting boundaries need parallel branch spawning — original activity continues, boundary event forks a new branch
- `BoundaryErrorEvent` needs an `IsInterrupting` flag (BPMN default is `true`)
- Compensation requires a compensation stack on `WorkflowInstanceState` and reverse execution logic
- Compensation is tightly coupled with Transaction Sub-Process (A2)

**Key insight:** Non-interrupting boundaries are a moderate extension. Compensation is deep and ties to A2.

---

## A5: Flat Variable State (no scoping)

**What breaks it:**

| BPMN Element | Why | Severity |
|---|---|---|
| Embedded Sub-Process | Child activities see parent variables but local variables don't leak. Variable shadowing. | HIGH |
| Multi-Instance Activity | Each of N instances needs its own variable scope. Results aggregated back. | HIGH |
| Data Objects / Data Stores | Typed, named containers with explicit lifecycle. Data Stores shared across processes. | MEDIUM |
| Loop Activity | Each iteration needs isolated variables and a loop counter. | MEDIUM |

**Architectural impact:**
- Variable state needs a scope tree — each scope has a parent, resolution walks up the chain
- Multi-instance needs per-instance variable slots plus aggregation
- Data Objects would require typed schemas alongside `ExpandoObject`

**Key insight:** Variable scoping is a prerequisite for Embedded Sub-Process (A2). These two assumptions are tightly coupled.

---

## Risk Matrix (Summary)

| Assumption | BPMN Elements Gated | Depth of Change | Priority |
|---|---|---|---|
| A1: Synchronous execution | Timer, Message, Signal, Event-Based Gateway | Deep — suspend/resume, scheduling, correlation | 1st |
| A2: Flat activities | Embedded Sub-Process, Transaction, Ad-Hoc, Multi-Instance | Deep — tree structure, recursive execution | 2nd |
| A5: Flat variables | Same as A2 + Data Objects/Stores | Deep — scope tree, per-instance slots | 2nd (coupled with A2) |
| A3: Binary conditions | Inclusive Gateway, Complex Gateway | Medium — token propagation for join counting | 3rd |
| A4: Interrupting-only | Non-interrupting boundaries, Escalation, Compensation | Medium to Deep — parallel spawning, compensation stack | 4th |

**Most impactful element to implement next:** Timer Events — forces A1 (suspend/resume), unblocks the most subsequent work, and addresses a common real-world need.

**Tightest coupling:** A2 + A5 — flat activities and flat variables must be solved together.

---

## Future Work Checklist

Each item solves exactly one problem. Items within a phase may depend on earlier items in the same phase. Phases are largely independent except where noted.

### Phase 1: Suspend/Resume Foundation (A1)

- [x] **1.1 — Workflow suspension model**: Activities that wait for external events (timer, message, signal) exit the execution loop naturally. Resume via `CompleteActivity()` or event handler. *Done.*
- [x] **1.2 — Timer Event (boundary, intermediate catch)**: Orleans Reminders schedule wake-ups via `TimerCallbackGrain`. *Done.*
- [x] **1.3 — Message correlation registry**: `MessageCorrelationGrain` maps `(messageName, correlationKey)` to workflow subscriptions. *Done.*
- [x] **1.4 — Message Event (intermediate catch, boundary)**: Activity registers in correlation grain, waits. Message delivery triggers completion. *Done.*
- [x] **1.5 — Signal broadcast mechanism**: `SignalCorrelationGrain` maintains `signalName` → subscriber set. Broadcast delivers to all. *Done.*
- [x] **1.6 — Signal Event (intermediate catch/throw, boundary)**: Activity subscribes to signal grain, waits. Signal throw completes all listeners. *Done.*
- [x] **1.7 — Event-Based Gateway**: Register for multiple events (timer + message + signal), first one to fire completes the gateway, cancel the others. *Done.*

### Phase 2: Nested Scopes (A2 + A5)

- [x] **2.1 — Tree-structured WorkflowDefinition**: SubProcess holds child Activities and SequenceFlows, implements IWorkflowDefinition. Recursive BpmnConverter parsing. *Done.*
- [x] **2.2 — Variable scope chain**: WorkflowVariablesState.ParentVariablesId chains scopes. GetVariable walks up. Writes go to local scope. *Done.*
- [x] **2.3 — Embedded Sub-Process**: SubProcess executes within same WorkflowInstance grain. ScopeId on ActivityInstanceEntry tracks nesting. Scope completion detection and boundary event cancellation. *Done.*
- [ ] **2.4 — Multi-Instance Activity (parallel)**: Single activity definition spawns N `ActivityInstance` grains, each with its own variable scope. Completion waits for all N. Results aggregated back. *Problem solved: parallel execution of same activity over a collection.*
- [ ] **2.5 — Multi-Instance Activity (sequential)**: Same as 2.4 but instances run one at a time. Loop variable tracks progress. *Problem solved: sequential iteration over a collection.*

### Phase 3: Dynamic Join Counting (A3)

- [ ] **3.1 — Token propagation**: Add a `TokenId` concept to `ActivityInstanceEntry`. When a gateway forks, it assigns token IDs to each branch. Join gateway counts arrived tokens. *Problem solved: runtime knowledge of active branches.*
- [ ] **3.2 — Inclusive Gateway**: Fork evaluates all conditions, activates 1..N matching flows (each with a token). Join waits for exactly the number of tokens that were actually created. *Problem solved: variable-count parallel branching.*

### Phase 4: Non-Interrupting & Compensation (A4)

- [ ] **4.1 — Non-interrupting boundary events**: Add `IsInterrupting` flag to `BoundaryErrorEvent`. When `false`, the attached activity continues; boundary event spawns a parallel branch. *Problem solved: parallel branch on boundary trigger without killing the source.*
- [ ] **4.2 — Escalation Event**: Non-interrupting boundary event with escalation semantics. Routes to escalation handler while attached activity continues. *Problem solved: escalation notifications without flow interruption.*
- [ ] **4.3 — Compensation stack**: Track completed activities with compensation handlers in an ordered stack on `WorkflowInstanceState`. *Problem solved: knowing what to undo and in what order.*
- [ ] **4.4 — Compensation Event + Transaction Sub-Process**: When transaction fails, invoke compensation handlers in reverse from the stack. Requires Phase 2 (Embedded Sub-Process). *Problem solved: transactional rollback of completed work.*

### Independent Items

- [ ] **5.1 — Data Objects**: Named, typed containers scoped to a process. Read/write via activity input/output. *Problem solved: explicit data lifecycle management.*
- [ ] **5.2 — Pools/Lanes**: Organizational metadata on activities. No execution impact — authorization/routing hints. *Problem solved: role-based activity assignment.*

---

## Runtime Correctness Risks (added 2026-02-23)

Risks in the current implementation independent of unimplemented BPMN elements. See `2026-02-23-state-durability-fix.md` for detailed analysis.

| ID | Risk | Severity | Status |
|----|------|----------|--------|
| C2 | **State loss on silo crashes** — `ExecuteWorkflow()` loop runs N activities with zero intermediate persistence. Crash mid-loop = all progress lost. | Critical | **Fixed** — `WriteStateAsync()` added after each `TransitionToNextActivity()` |
| C3 | ~~Parallel gateway join race conditions~~ | ~~Critical~~ | **False alarm** — Orleans turn-based concurrency prevents this; grain is not `[Reentrant]` |
| H1 | **Message correlation bottleneck** — single `MessageCorrelationGrain` per message name handles all workflows. | High | Open |
| H3 | **Signal/message delivery fire-and-forget** — subscriptions cleared before delivery; delivery failures = permanent signal loss. `MessageCorrelationGrain` documents this as "at-most-once" semantics. | High | Open |
| H4 | **No activity idempotency enforcement** — "at most once" execution is convention, not enforced. | High | Open |
| M3 | **No API rate limiting** — `POST /message` and `POST /signal` endpoints unprotected. | Medium | Open |
| M5 | **No workflow versioning for running instances** — running instances cache the definition on activation; redeployment causes version drift. | Medium | Open |
