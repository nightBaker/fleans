# Manual Test Plan — Rate-limiting table audit (Issue #401)

Verifies that the policy → endpoint mapping table at `api.md#policy--endpoint-mapping` matches `Fleans.Api/Controllers/WorkflowController.cs` 1:1 (17 `[EnableRateLimiting(...)]` attributes → 5 policy rows), and that future regressions of the three bugs this PR closes are caught.

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

**Expect:** the line preceding `complete-activity` matches `TaskOperation`. If `WorkflowMutation` appears as the closest preceding policy header, the misclassification has returned. (Source-of-truth: `WorkflowController.cs:98` is `[EnableRateLimiting("task-operation")]`.)

### 3. Fictional-endpoint regression-guard — `/upload-bpmn` MUST NOT appear

```bash
grep -c 'upload-bpmn' website/dist/reference/api/index.html
```

**Expect:** **0**. If this is ever ≥ 1, the fictional `/upload-bpmn` endpoint has reappeared. No such endpoint exists in `src/Fleans/Fleans.Api/Controllers/`.

### 4. Read row completeness — all 5 read endpoints present

```bash
for path in '/definitions"' \
            'definitions/{key}/instances' \
            'definitions/{key}/{version}/instances' \
            '/tasks"' \
            'tasks/{activityInstanceId}"'; do
  echo "$path: $(grep -c "$path" website/dist/reference/api/index.html)"
done
```

**Expect:** each path appears at least once. (The exact patterns above are tightened to avoid double-matches against the longer paths; adjust if the table renders the params differently.)

### 5. Path-style consistency — no `/Workflow` prefix in the new table

```bash
grep -E 'href="?/Workflow/' website/dist/reference/api/index.html
```

**Expect:** no matches inside the policy → endpoint mapping table block. The table dropped all `/Workflow` prefixes; the explicit "All paths below are relative to the `/Workflow` controller route." note above the table establishes the convention.

### 6. Drift-guard — `WorkflowController.cs` attribute count matches doc claim

```bash
grep -cE '\[EnableRateLimiting\(' src/Fleans/Fleans.Api/Controllers/WorkflowController.cs
```

**Expect:** **17**. Distribution by policy:
- `workflow-mutation` — 5 (lines 32, 49, 67, 83, 113)
- `task-operation` — 4 (lines 98, 200, 223, 236)
- `read` — 5 (lines 133, 146, 160, 174, 189)
- `admin` — 2 (lines 262, 273)
- `polling` — 1 (line 284)

If the count drifts (a new endpoint added or an existing one reclassified), the table needs a corresponding edit; the drift-guard HTML comment immediately above the table pins the line range.

### 7. Both themes render the table

`npm run dev` and visit `/fleans/reference/api/#policy--endpoint-mapping`. Toggle light/dark via the navbar theme switch.

**Expect:** the table renders with all 5 rows visible in both themes; `<code>` styling for paths is readable.

## Verdict

- **PASSED** — all 7 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
