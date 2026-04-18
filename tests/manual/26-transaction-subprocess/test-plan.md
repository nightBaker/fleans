# Manual Test 26 — Transaction Sub-Process (Happy Path)

## Scenario

A process runs a setup script, then enters a **Transaction Sub-Process** containing
two sequential script tasks (validate → process), then runs a confirmation step
outside the transaction. The workflow must complete via the normal path, all
transaction-scoped variables must be merged into the parent scope, and the engine
must record a `Completed` transaction outcome for the Transaction activity instance.

> **Phase 1 scope:** This test covers only the Completed outcome (happy path).
> Cancel (triggered by Cancel End Event — issue #230) and Hazard (unhandled error
> escape — issue #231) paths are tested when those features land.

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `happy-path.bpmn` available in this folder
- A clean dev DB (delete SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`)

## Steps

1. **Deploy** the BPMN: upload `happy-path.bpmn` via the Web UI
   (Fleans.Web → Deployments → Upload), or:
   ```
   POST https://localhost:7140/Workflow/upload-bpmn
   ```
   Confirm `txHappyPathProcess` appears in the deployments list.

2. **Start an instance**:
   ```
   POST https://localhost:7140/Workflow/start
   Body: {"WorkflowId":"txHappyPathProcess"}
   ```
   Note the returned `workflowInstanceId`.

3. **Observe automatic execution** — all tasks in this workflow are script tasks
   that complete automatically. The workflow should reach a terminal state within
   a second. Open the instance in the Web UI and confirm it is Completed.

4. **Verify the activity state** in the Web UI instance detail:
   - All expected activities should appear in the Completed list.
   - The Transaction Sub-Process scope (`paymentTransaction`) should be completed.

5. **Inspect variables** in the Web UI — the root scope should contain:
   - `requestId = "tx-001"`
   - `amount = 100`
   - `validated = true`
   - `processed = true`
   - `result = "SUCCESS"`
   - `confirmed = true`

## Expected Outcomes Checklist

- [ ] Workflow instance state is **Completed**.
- [ ] `setupTask` shows as **Completed**.
- [ ] `validateTask` shows as **Completed** (inside the transaction scope).
- [ ] `processTask` shows as **Completed** (inside the transaction scope).
- [ ] `tx_end` (transaction end event) shows as **Completed**.
- [ ] `paymentTransaction` host shows as **Completed**.
- [ ] `confirmTask` shows as **Completed** (outside, after transaction).
- [ ] `end` event reached — workflow terminated normally.
- [ ] All transaction-scoped variables (`validated`, `processed`, `result`) are
      visible in the root scope after the transaction completes (variable merge).
- [ ] No residual active activities remain.
- [ ] No errors or exceptions in Aspire dashboard logs for this instance.
