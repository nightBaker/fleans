# 62 — Chart streaming-provider wiring

Verifies #599: the Helm chart's `_helpers.tpl` switch emits `Fleans__Streaming__Provider` for **all four** values (Memory, Redis, Kafka, AzureQueue) — not just Kafka — and aborts `helm template` with a clear error on typos.

## Prerequisites

- `helm` ≥ 3.12 installed (`helm version --short`).
- Chart sources at `charts/fleans/` (this plan exercises `helm template` only — no live cluster needed).

## Steps

### 1. Redis explicit

```bash
helm template fleans charts/fleans --set streaming.provider=Redis 2>&1 \
  | grep -E "Fleans__Streaming__Provider|Fleans__Streaming__Kafka|Fleans__Streaming__AzureQueue" | head
```

- [ ] One or more `Fleans__Streaming__Provider` lines with value `"Redis"` rendered (every workload pod spec consumes `fleans.commonEnv`).
- [ ] **No** `Fleans__Streaming__Kafka__Brokers` lines.
- [ ] **No** `Fleans__Streaming__AzureQueue__ConnectionString` lines.

### 2. Kafka regression check

```bash
helm template fleans charts/fleans \
  --set streaming.provider=Kafka \
  --set streaming.kafka.brokers=kafka.kafka.svc:9092 2>&1 \
  | grep -E "Fleans__Streaming__Provider|Fleans__Streaming__Kafka" | head
```

- [ ] `Fleans__Streaming__Provider=Kafka` rendered.
- [ ] `Fleans__Streaming__Kafka__Brokers="kafka.kafka.svc:9092"` rendered.

### 3. AzureQueue

```bash
helm template fleans charts/fleans \
  --set streaming.provider=AzureQueue \
  --set streaming.azureQueue.connectionString="DefaultEndpointsProtocol=https;AccountName=test" 2>&1 \
  | grep -E "Fleans__Streaming__Provider|Fleans__Streaming__AzureQueue" | head
```

- [ ] `Fleans__Streaming__Provider=AzureQueue` rendered.
- [ ] `Fleans__Streaming__AzureQueue__ConnectionString="DefaultEndpointsProtocol=https;AccountName=test"` rendered.

### 4. Chart default (no `--set`)

```bash
helm template fleans charts/fleans 2>&1 \
  | grep -E "Fleans__Streaming__Provider" | head
```

- [ ] `Fleans__Streaming__Provider=Redis` rendered (the rebased chart default per #599 round 2).

### 5. Case-insensitive match

```bash
helm template fleans charts/fleans --set streaming.provider=redis 2>&1 \
  | grep -E "Fleans__Streaming__Provider" | head
```

- [ ] `Fleans__Streaming__Provider=Redis` rendered (canonical capitalised value, even though operator passed lowercase).

### 6. Memory explicit (regression target for round-1 🔴 finding)

```bash
helm template fleans charts/fleans --set streaming.provider=Memory 2>&1 \
  | grep -E "Fleans__Streaming__Provider" | head
```

- [ ] `Fleans__Streaming__Provider=Memory` IS emitted (pre-fix: nothing was emitted, silo silently defaulted to Redis).

### 7. Typo path

```bash
helm template fleans charts/fleans --set streaming.provider=Reddis 2>&1; echo "exit=$?"
```

- [ ] Exit code non-zero.
- [ ] Error message contains `"Unsupported streaming.provider"` and names the four valid providers (Memory, Redis, Kafka, AzureQueue).

## Pass criteria

All 7 step checklists pass. The critical regressions:

- Step 6 was the round-1 silent-fallback hole (Memory chosen → silo ran Redis); pre-fix this assertion was inverted.
- Step 7 was completely absent pre-fix (typo silently fell through to no env var).

## Failure modes

- Step 1 fails with no `Fleans__Streaming__Provider` lines → the switch in `_helpers.tpl` isn't matching `redis` — check the case-folding (`lower $provider`).
- Step 4 fails with `Memory` instead of `Redis` → the `values.yaml` default rebase didn't ship; confirm `streaming.provider: Redis` in the diff.
- Step 7 fails with exit 0 → the `helm fail` validation block isn't wired; confirm the `if not (has $provider (list ...))` block precedes the canonical switch in `_helpers.tpl`.
