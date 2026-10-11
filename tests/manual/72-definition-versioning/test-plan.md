# Manual Test Plan — #72 Process-definition versioning

Verifies #767: running instances finish on the version they started on after a
newer version of the same key is deployed; new starts use the latest version;
`GET /Definitions/{key}/instances` and `GET /Definitions/{key}/{version}/instances`
list each instance under the right version; call activities resolve the
**latest** child version at the moment the call activity executes (no pinning —
matches `website/src/content/docs/guides/call-activities-and-subprocesses.md#versioning`).

Automated by `src/Fleans/Fleans.E2E.Tests/Specs/DefinitionVersioningTests.cs`. The
spec rewrites the process ids with a per-run suffix (the E2E SQLite file persists
across runs); when running this plan manually on a clean DB, use the fixtures as-is.

## Fixtures

| File | Process id | Shape |
|---|---|---|
| `versioned-order-v1.bpmn` | `versioned-order` | start → `v1Prepare` → `approve` (user task) → `v1Finish` → `v1End` |
| `versioned-order-v2.bpmn` | `versioned-order` | start → `v2Prepare` → `approve` (user task) → `v2Finish` → `v2Audit` → `v2End` |
| `versioned-child-v1.bpmn` | `versioned-child` | `childStart` → `childV1Work` (`result="child-v1"`) → `childHold` (user task) → `childV1End` |
| `versioned-child-v2.bpmn` | `versioned-child` | `childStart` → `childV2Work` (`result="child-v2"`) → `childV2End` |
| `versioned-parent.bpmn` | `versioned-parent` | `parentStart` → `gate` (user task) → `callChild` (`calledElement="versioned-child"`, output `result → childResult`) → `parentEnd` |

Both `versioned-order` versions use the same user-task id `approve` on purpose: if a
v1 instance were migrated onto the v2 graph, completing `approve` would route to
`v2Finish` instead of `v1Finish`.

User tasks have no assignee/candidate constraints — claim with any user id via
`POST /UserTasks/{activityInstanceId}/claim` then `POST /UserTasks/{activityInstanceId}/complete`.
The `activityInstanceId` comes from `GET /Instances/{id}/state` → `activeActivities`.

## Part A — running instances keep their version

1. `POST /Definitions/deploy` with `versioned-order-v1.bpmn` → `{"processDefinitionKey":"versioned-order","version":1}`.
2. `POST /Execution/start` `{"WorkflowId":"versioned-order"}` twice → instances **A** and **B**.
   Each reaches `approve` (active) with `v1Prepare` completed.
3. Deploy `versioned-order-v2.bpmn` → `version: 2`.
4. Start instance **C** → `approve` active, `v2Prepare` completed, `v1Prepare` **not** completed.
5. Claim + complete `approve` on A and B.
   - [ ] Both complete with `start, v1Prepare, approve, v1Finish, v1End` completed.
   - [ ] Neither has `v2Prepare / v2Finish / v2Audit / v2End` completed; `finishedBy = "v1"`; no `audited` variable.
6. Claim + complete `approve` on C.
   - [ ] C completes with `v2Prepare, approve, v2Finish, v2Audit, v2End`; no `v1*` ids; `finishedBy = "v2"`, `audited = True`.
7. Listings:
   - [ ] `GET /Definitions/versioned-order/1/instances` → exactly A and B (all completed).
   - [ ] `GET /Definitions/versioned-order/2/instances` → exactly C.
   - [ ] `GET /Definitions/versioned-order/instances` → A, B and C (`totalCount: 3`).
   - [ ] `GET /Definitions/versioned-order/3/instances` → `totalCount: 0`.
   - [ ] A and B share one `processDefinitionId`; C has a different one.

## Part B — call activity resolves the latest child version at call time

1. Deploy `versioned-child-v1.bpmn` (→ v1) and `versioned-parent.bpmn`.
2. Start parent **P1**; complete its `gate`. P1 sits on `callChild`.
   `GET /Definitions/versioned-child/1/instances` lists one child **C1**, active on `childHold`.
3. Start parent **P2**; it waits on `gate`.
4. Deploy `versioned-child-v2.bpmn` (→ v2).
5. Complete P2's `gate`.
   - [ ] P2 completes; `childResult = "child-v2"` (latest at call time, not the version that was latest when P2 started).
   - [ ] `GET /Definitions/versioned-child/2/instances` lists one completed child with `childV2Work, childV2End` completed and no `childV1*` ids.
6. Complete C1's `childHold`.
   - [ ] C1 completes on the v1 graph (`childV1Work, childHold, childV1End`; no `childV2*`), keeping its v1 `processDefinitionId`.
   - [ ] P1 completes with `childResult = "child-v1"`.
7. - [ ] `GET /Definitions/versioned-child/instances` lists both children; the v1 listing still contains only C1.
