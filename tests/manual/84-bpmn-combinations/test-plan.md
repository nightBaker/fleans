# Manual Test Plan — BPMN element combinations

Elements that other plans cover one at a time, combined the way real processes use them: a loop
through a waiting catch event, and interrupting timers on scopes that hold waiting elements.
Automated by `BpmnCombinationTests` (E2E) and, in more detail, by the Application tests
`LoopTests`, `ScopeCancellationCleanupTests` and `MultiInstanceBodyTests`.

## Prerequisites

Aspire stack running: `dotnet run --project Fleans.Aspire` from `src/Fleans/`.

## A. Message catch in a loop (`message-catch-loop.bpmn`)

| # | Action | Expected |
|---|--------|----------|
| 1 | Deploy `message-catch-loop.bpmn`; start it with `{"orderId": "loop-1"}` | Instance waits on `wait_payment` |
| 2 | `POST /Execution/message` `{"messageName": "loopPayment", "correlationKey": "loop-1"}` | `delivered: true`; `count` runs; `wait_payment` is active again (pass 2) |
| 3 | Send the same message twice more, waiting for `wait_payment` to be active before each | Each send is delivered once; after the third the instance completes through `end`, with 3 `wait_payment` completions |

## B. Timer on a sub-process with a user task (`subprocess-user-task-timeout.bpmn`)

| # | Action | Expected |
|---|--------|----------|
| 1 | Deploy and start `subprocess-user-task-timeout.bpmn` | `approve` appears in `GET /UserTasks?assignee=timeout-reviewer` |
| 2 | Wait 5 s | The timer interrupts `review`; the instance completes through `escalated_end` |
| 3 | `GET /UserTasks?assignee=timeout-reviewer` | The `approve` task is gone |

## C. Timer on a call activity (`call-activity-timeout.bpmn` + `call-activity-timeout-child.bpmn`)

> **KNOWN BUG:** [#847](https://github.com/nightBaker/fleans/issues/847) — interrupting a call activity does not cancel the child instance; step 3 fails.

| # | Action | Expected |
|---|--------|----------|
| 1 | Deploy `call-activity-timeout-child.bpmn`, then `call-activity-timeout.bpmn`; start the parent | The parent waits on `call_child`; a child instance waits on `child_work` (user task for `john`) |
| 2 | Wait 5 s | The parent completes through `timed_out` |
| 3 | Open the child instance | It has no active activities, and `child_work` is not in `GET /UserTasks?assignee=john` |
