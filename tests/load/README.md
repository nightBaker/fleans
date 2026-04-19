# Fleans Load Tests

k6 load test suite for the Fleans workflow engine.

## Prerequisites

- [k6](https://k6.io/docs/get-started/installation/) installed
- Fleans cluster running (see `docker-compose` in issue #237, or Aspire for local dev)
- All 3 BPMN fixtures deployed (run `setup.js` first)

## Scripts

| Script | Purpose | Scenario |
|--------|---------|---------|
| `scripts/setup.js` | Deploy BPMN fixtures | Run once before any scenario |
| `scripts/linear.js` | Scenario 1: linear throughput | Ramp to 500 VUs, measure start latency |
| `scripts/metrics.js` | Shared metric definitions | Imported by scenario scripts |

## Fixtures

| File | Process key | Description |
|------|------------|-------------|
| `fixtures/linear-workflow.bpmn` | `load-linear` | Simple start → script → end |
| `fixtures/parallel-workflow.bpmn` | `load-parallel` | Parallel gateway fork/join |
| `fixtures/events-workflow.bpmn` | `load-events` | Timer + message events |

## Running

### 1. Deploy fixtures (required once per cluster restart)

```bash
k6 run --insecure-skip-tls-verify tests/load/scripts/setup.js
```

Override the target URL with `K6_TARGET_URL`:

```bash
K6_TARGET_URL=https://my-cluster.example.com k6 run tests/load/scripts/setup.js
```

### 2. Run linear throughput scenario

**Cloud / Docker Compose cluster (full load):**
```bash
k6 run --insecure-skip-tls-verify tests/load/scripts/linear.js
```

**Local connectivity check (safe for dev):**
```bash
k6 run --vus 5 --iterations 20 --insecure-skip-tls-verify tests/load/scripts/linear.js
```

> **Warning:** Do NOT run the full 500-VU scenario against a local single-node dev setup.
> It generates ~5 000 workflow starts/second and will overwhelm a dev database.

## Thresholds

Shared baseline thresholds are defined in `thresholds.json` and imported by all scenario scripts:

| Metric | Threshold |
|--------|-----------|
| `http_req_failed` | `rate < 1%` |
| `http_req_duration` | `p(95) < 2 000 ms` |
| `workflow_start_duration` | `p(95) < 2 000 ms` |

## Related issues

- Issue #237 — Docker Compose infrastructure setup
- Issue #238 — BPMN fixtures
- Issue #239 — This script: setup + linear scenario
- Issue #240 — Parallel branching scenario
- Issue #241 — Event-driven scenario
- Issue #242 — Mixed workload scenario
- Issue #244 — Cloud validation run
