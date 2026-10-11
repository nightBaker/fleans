# Manual Test Plan — Rate-limiting table audit (Issue #401)

Verifies that the policy → endpoint mapping table at `api.md#policy--endpoint-mapping` matches the controllers in `Fleans.Api/Controllers/` 1:1 (19 `[EnableRateLimiting(...)]` attributes → 5 policy rows). The original `WorkflowController` was split into `DefinitionsController`, `ExecutionController`, `InstancesController` and `UserTasksController` (#587, #792), and that future regressions of the three bugs this PR closes are caught.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors, page emitted to `dist/reference/api/index.html`.

### 2. Misclassification regression-guard — `/complete-activity` is in TaskOperation, NOT WorkflowMutation

```bash
grep -B1 'complete-activity' website/dist/reference/api/index.html | grep -i 'TaskOperation\|WorkflowMutation'
```

**Expect:** the line preceding `complete-activity` matches `TaskOperation`. If `WorkflowMutation` appears as the closest preceding policy header, the misclassification has returned. (Source of truth: `ExecutionController.cs` marks `complete-activity` with `[EnableRateLimiting("task-operation")]`.)

### 3. Fictional-endpoint regression-guard — `/upload-bpmn` MUST NOT appear

```bash
grep -c 'upload-bpmn' website/dist/reference/api/index.html
```

**Expect:** **0**. If this is ever ≥ 1, the fictional `/upload-bpmn` endpoint has reappeared. No such endpoint exists in `src/Fleans/Fleans.Api/Controllers/`.

### 4. Read row completeness — all 5 read endpoints present

```bash
for path in '/Definitions<' \
            'Definitions/{key}/instances' \
            'Definitions/{key}/{version}/instances' \
            '/UserTasks<' \
            'UserTasks/{activityInstanceId}<'; do
  echo "$path: $(grep -c "$path" website/dist/reference/api/index.html)"
done
```

**Expect:** each path appears at least once. (The exact patterns above are tightened to avoid double-matches against the longer paths; adjust if the table renders the params differently.)

### 5. Path-style consistency — no removed `/Workflow/` routes

```bash
grep -c '/Workflow/' website/dist/reference/api/index.html
```

**Expect:** only the historical mention of `/Workflow/tasks/...` (if any). Every table path carries its controller prefix (`/Definitions`, `/Execution`, `/Instances`, `/UserTasks`).

### 6. Drift-guard — controller attribute count matches doc claim

```bash
grep -hoE 'EnableRateLimiting\("[a-z-]+"\)' src/Fleans/Fleans.Api/Controllers/*.cs | sort | uniq -c
```

**Expect:** **19** in total:
- `workflow-mutation` — 5 (`Execution/start`, `message`, `signal`, `evaluate-conditions`; `Definitions/deploy`)
- `task-operation` — 6 (`Execution/complete-activity`; `UserTasks/{id}/claim`, `unclaim`, `complete`, `fail`, `cancel`)
- `read` — 5 (`GET /Definitions`, `/Definitions/{key}/instances`, `/Definitions/{key}/{version}/instances`; `GET /UserTasks`, `/UserTasks/{id}`)
- `admin` — 2 (`Definitions/disable`, `enable`)
- `polling` — 1 (`Instances/{id}/state`)

If the count drifts (a new endpoint added or an existing one reclassified), the table needs a corresponding edit.

### 7. Both themes render the table

`npm run dev` and visit `/fleans/reference/api/#policy--endpoint-mapping`. Toggle light/dark via the navbar theme switch.

**Expect:** the table renders with all 5 rows visible in both themes; `<code>` styling for paths is readable.

## Verdict

- **PASSED** — all 7 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
