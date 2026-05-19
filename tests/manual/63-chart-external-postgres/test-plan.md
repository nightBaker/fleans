# 63 — Chart external-Postgres support

Verifies #601: when `persistence.provider=Postgres` but `postgres.enabled=false`, the chart no longer injects `ConnectionStrings__fleans` from a chart-managed secret that doesn't exist. Operators can then supply the connection string themselves via `extraEnv` and the silo boots cleanly.

## Prerequisites

- `helm` ≥ 3.12 installed (`helm version --short`).
- Chart sources at `charts/fleans/` (this plan exercises `helm template` only — no live cluster needed).

## Steps

### 1. Chart-managed Postgres (regression check — unchanged path)

```bash
helm template fleans charts/fleans \
  --set persistence.provider=Postgres \
  --set postgres.enabled=true 2>&1 \
  | grep -A4 "ConnectionStrings__fleans" | head -10
```

- [ ] `ConnectionStrings__fleans` env block IS rendered.
- [ ] Block uses `valueFrom.secretKeyRef.name=<release>-postgres` AND `key: connection-string` (not just the env-var name — the key wire matters per analysis-review 🟢 #4).

### 2a. External Postgres — literal `value:` extraEnv

```bash
helm template fleans charts/fleans \
  --set persistence.provider=Postgres \
  --set postgres.enabled=false \
  --set 'extraEnv[0].name=ConnectionStrings__fleans' \
  --set 'extraEnv[0].value=Host=external-pg;Database=fleans' 2>&1 \
  | grep -A2 "ConnectionStrings__fleans" | head -10
```

- [ ] `ConnectionStrings__fleans` IS rendered with the literal `value: Host=external-pg;Database=fleans`.
- [ ] **No** `secretKeyRef:` block referencing `connection-string` (the chart-managed secret reference is correctly skipped — this was the silent footgun pre-fix).

### 2b. External Postgres — `valueFrom.secretKeyRef` extraEnv (GitOps-friendly recipe)

```bash
helm template fleans charts/fleans \
  --set persistence.provider=Postgres \
  --set postgres.enabled=false \
  --set 'extraEnv[0].name=ConnectionStrings__fleans' \
  --set 'extraEnv[0].valueFrom.secretKeyRef.name=my-external-pg-secret' \
  --set 'extraEnv[0].valueFrom.secretKeyRef.key=connection-string' 2>&1 \
  | grep -A4 "ConnectionStrings__fleans" | head -10
```

- [ ] `ConnectionStrings__fleans` IS rendered with `valueFrom.secretKeyRef.name=my-external-pg-secret` AND `key=connection-string`.
- [ ] Exactly ONE `ConnectionStrings__fleans` env per workload (no duplicate from a chart-managed secretKeyRef).
- [ ] Matches the recipe documented in `charts/fleans/README.md` "External Postgres" section AND in `website/src/content/docs/reference/persistence.mdx` "External PostgreSQL" subsection.

### 3. Explicit Sqlite path (negative regression check)

```bash
helm template fleans charts/fleans --set persistence.provider=Sqlite 2>&1 \
  | grep -E "Persistence__Provider|ConnectionStrings__fleans" | head -5
```

- [ ] `Persistence__Provider="Sqlite"` IS emitted (always rendered regardless of provider).
- [ ] **No** `ConnectionStrings__fleans` lines (Sqlite path doesn't go through the Postgres conditional).

### 4. helm lint

```bash
helm lint charts/fleans
```

- [ ] Output ends with `1 chart(s) linted, 0 chart(s) failed`.

## Pass criteria

All 4 step checklists pass. The critical regression: pre-fix, step 2a/2b would render BOTH the chart-managed secretKeyRef (pointing at a non-existent secret → pod stuck in `CreateContainerConfigError`) AND the user-supplied extraEnv. Post-fix, only the user-supplied entry exists.

## Operational verification (out-of-band)

The issue body asks for `kind`-cluster verification before merging. Per the manual-test-plan convention this is out-of-band — `helm template` assertions above are the per-PR gate. Operators can additionally run:

```bash
kind create cluster --name fleans-test
kubectl create secret generic my-external-pg-secret \
  --from-literal=connection-string='Host=<external-host>;Port=5432;Database=fleans;Username=fleans;Password=...'
helm install fleans charts/fleans \
  --set persistence.provider=Postgres \
  --set postgres.enabled=false \
  --set 'extraEnv[0].name=ConnectionStrings__fleans' \
  --set 'extraEnv[0].valueFrom.secretKeyRef.name=my-external-pg-secret' \
  --set 'extraEnv[0].valueFrom.secretKeyRef.key=connection-string'
kubectl wait --for=condition=Ready pod -l app.kubernetes.io/instance=fleans --timeout=120s
```

A healthy pod readiness + an Aspire health endpoint returning 200 confirms the external connection string was consumed.

## Failure modes

- Step 1 fails (no env rendered) → the `and` clause in `_helpers.tpl:102` was applied incorrectly to break the chart-managed default path. Verify the conditional reads `if and (eq ... "postgres") .Values.postgres.enabled`.
- Step 2a/2b fail with TWO `ConnectionStrings__fleans` entries (one from `secretKeyRef`, one from `extraEnv`) → the conditional wasn't actually updated; check the diff at `_helpers.tpl:102`.
- Step 3 fails with a `ConnectionStrings__fleans` line emitted → the conditional's first clause (`eq ... "postgres"`) failed; verify `lower` is applied.
