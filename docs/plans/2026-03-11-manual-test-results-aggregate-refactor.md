# Manual Test Results — Workflow Execution Aggregate Refactor

**Date:** 2026-03-11
**Branch:** `feature/workflow-execution-aggregate` (PR #128)
**Purpose:** Verify no regressions after consolidating WorkflowInstance grain to use WorkflowExecution aggregate (6 partial files → 3).

## Test Results

| # | Test | Result | Notes |
|---|------|--------|-------|
| 01 | Basic Workflow (start→task→end) | **PASSED** | 3 activities completed |
| 02 | Script Tasks (variable mutation) | **PASSED** | x=15, greeting="hello" |
| 03 | Exclusive Gateway (conditional) | **PASSED** | highTask path taken, lowTask skipped |
| 04 | Parallel Gateway (fork/join) | **PASSED** | Both branches + afterJoin completed |
| 05 | Event-Based Gateway (timer vs msg) | **PASSED** | Message path won, timer path not taken |
| 06 | Call Activity (parent→child) | **PASSED** | Parent + child completed |
| 07 | Embedded SubProcess | **PASSED** | subStart, subScript, subEnd, afterSub all completed |
| 08A | Timer Intermediate Catch (5s) | **PASSED** | Timer fired, afterTimer completed |
| 08B | Timer Boundary on IntermediateCatch | **KNOWN BUG** | Boundary timer doesn't register (pre-existing) |
| 09A | Message Catch (correlated) | **PASSED** | Message delivered, waitApproval→afterApproval |
| 09B | Message Boundary on IntermediateCatch | **KNOWN BUG** | Skipped — pre-existing bug |
| 10A | Signal Catch (broadcast) | **PASSED** | Signal delivered, afterSignal completed |
| 10B | Signal Boundary on IntermediateCatch | **KNOWN BUG** | Skipped — pre-existing bug |
| 11 | Error Boundary on CallActivity | **PASSED** | errorBoundary→errorHandler→end2 completed |
| 12 | Variable Scoping (parallel isolation) | **PASSED** | Both branches + join completed |
| 13A | Multi-Instance Parallel (cardinality) | **PASSED** | 4 repeatTask instances completed |
| 13B | Multi-Instance Parallel (collection) | **PASSED** | 4 processItem instances completed |
| 13C | Multi-Instance Sequential (collection) | **PASSED** | 4 sequential processItem completed |
| 14A | Inclusive Gateway (parallel conditions) | **PASSED** | branch1 + branch2 → join → afterJoin |
| 14B | Inclusive Gateway (default flow) | **PASSED** | defaultTask → defaultEnd |
| 14C | Nested Inclusive Gateway | **PASSED** | All inner/outer branches completed |
| 15A | Non-Interrupting Message Boundary | **PASSED** | handleMessage fired, longTask still active |
| 15B | Non-Interrupting Timer Boundary | **PASSED** | sendReminder fired, longTask still active |
| 15C | Timer Cycle (R3/PT5S) | **BUG** | Cycle timer did not fire after 20s wait |
| 16 | Message Start Event | **PASSED** | Instance created and completed via message |
| 17 | Signal Start Event | **PASSED** | Instance created and completed via signal |

## Summary

- **22 passed** — no regressions from the aggregate refactor
- **3 known bugs** (pre-existing): boundary events on IntermediateCatchEvents don't register subscriptions (08B, 09B, 10B)
- **1 potential new bug**: timer cycle (R3/PT5S) in test 15C did not fire — needs investigation

## Known Bugs (Pre-Existing)

These are documented in `docs/plans/2026-02-25-manual-test-results.md`:

1. **Boundary events on IntermediateCatchEvents don't register subscriptions** — affects timer/message/signal boundaries attached to intermediate catch events
2. **Child process errors don't propagate to parent error boundary on CallActivity** — test 11 passed via a different code path (direct error boundary on the call activity itself)

## New Issue Found

### Timer Cycle Not Firing (Test 15C)

- **Workflow:** `ni-timer-cycle-test` with `R3/PT5S` cycle boundary on a task
- **Expected:** Timer fires 3 times at 5s intervals, `sendReminder` executes each time
- **Actual:** After 20+ seconds, no cycle timer fires occurred; only `start` completed, `longTask` active
- **Impact:** Timer cycle non-interrupting boundaries may be broken
- **Action:** Investigate `TimerCallbackGrain` cycle handling and whether the aggregate refactor affected timer re-registration
