# Fleans Load Tests

k6 load test suite for the Fleans workflow engine.

## Prerequisites

- [k6](https://k6.io/docs/get-started/installation/) installed
- Fleans cluster running (see `docker-compose` in issue #237, or Aspire for local dev)
- All 3 BPMN fixtures deployed (run `setup.js` first)

## Scripts

| Script | Purpose | Invocation |
|--------|---------|---------|
| `scripts/setup.js` | Deploy BPMN fixtures | `k6 run scripts/setup.js` (run once) |
| `scripts/linear.js` | Scenario 1: linear throughput | `k6 run scripts/linear.js` |
| `scripts/events.js` | Scenario 3 — event-driven with message correlation | `k6 run scripts/events.js` |
| `scripts/mixed.js` | Mixed workload combining all scenarios | `k6 run scripts/mixed.js` |
| `scripts/metrics.js` | Shared metric definitions | Imported by scenario scripts |

**`setup.js`** (from #239) is a standalone file you run once via `k6 run setup.js` to deploy fixtures and verify the cluster is ready.

**`setup()`** exported from `events.js` is k6's per-run init hook — it verifies the `load-events` process is deployed and active before spawning VUs. Both are required but invoked at different times.

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

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `K6_TARGET_URL` | `https://localhost:7140` | Base URL of the Fleans API |
| `K6_POLL_INTERVAL_MS` | `100` | Initial poll interval (ms) |
| `K6_POLL_MAX_ATTEMPTS` | `20` | Safety ceiling for poll iterations |
| `K6_POLL_BACKOFF_CAP_MS` | `500` | Max backoff per poll sleep |
| `K6_POLL_TOTAL_BUDGET_MS` | `3000` | Wall-clock budget for the entire poll phase |
| `K6_MESSAGE_RETRY_BUDGET_MS` | `1000` | Wall-clock budget for message delivery retries on 404 |
| `K6_MESSAGE_RETRY_INTERVAL_MS` | `150` | Sleep between message retry attempts |

All env vars must be unset or set to a positive integer. An empty string falls back to the default.

## Thresholds

Shared baseline thresholds are defined in `thresholds.json` and imported by all scenario scripts:

| Metric | Type | Threshold | Notes |
|---|---|---|---|
| `http_req_failed` | Rate | `rate < 1%` | |
| `http_req_duration` | Trend | `p(95) < 2 000 ms` | |
| `workflow_start_duration` | Trend | `p(95) < 2 000 ms` | HTTP duration of `POST /Workflow/start` |
| `poll_until_catch_duration` | Trend | `p(95) < 2 500 ms` | Wall-clock from start to poll success |
| `message_accept_duration` | Trend | `p(95) < 2 000 ms` | HTTP duration of final `POST /Workflow/message` |
| `message_retry_attempts` | Trend | *(none)* | Diagnostic only |
| `poll_stalls` | Rate | `rate < 1%` | Poll budget expired |
| `correlation_miss` | Rate | `rate < 1%` | Message retry budget expired |

## Rate-limit policy: `polling`

The `GET /Workflow/instances/{id}/state` endpoint uses `[EnableRateLimiting("polling")]`. This attribute is **opt-in** via the `RateLimiting:Polling` config section:

- **Section entirely absent** (default) → `UseRateLimiter()` is not registered; attribute is a no-op. Fails open.
- **Section populated for `Polling` but missing `WorkflowMutation`/`TaskOperation`/`Read`/`Admin`** → `UseRateLimiter()` activates, but requests to endpoints with unregistered policy names throw `InvalidOperationException` → HTTP 500. Fails closed.
- **All five policies populated** → full opt-in enforcement.

Load-test deployments must populate all five together; production deployments should either populate all five or leave the section absent.

### Rate-limit sizing (for docker-compose load profile)

Target: 200 VU × 5 min sustained.

| Policy | Peak rate | Compose env (`Window=1`) |
|---|---|---|
| `WorkflowMutation` | ~133 rps (200 VU × 2 mutations/iter / ~3s iter) | `RateLimiting__WorkflowMutation__PermitLimit=1000` |
| `TaskOperation` | 0 in events scenario | `RateLimiting__TaskOperation__PermitLimit=100` |
| `Read` | `setup()` × 1 + admin | `RateLimiting__Read__PermitLimit=100` |
| `Admin` | 0 | `RateLimiting__Admin__PermitLimit=100` |
| `Polling` | ~1333 rps (200 VU × 20 polls / 3s budget) | `RateLimiting__Polling__PermitLimit=10000` |

## Dev-host runs

When running against Aspire locally (without docker-compose), rate limiting is off by default because `appsettings.json` has no `RateLimiting` section. Do **not** partially populate the section — either set all five policies or leave it absent entirely.

## Related issues

- Issue #237 — Docker Compose infrastructure setup
- Issue #238 — BPMN fixtures
- Issue #239 — This script: setup + linear scenario
- Issue #240 — Parallel branching scenario
- Issue #241 — Event-driven scenario
- Issue #242 — Mixed workload scenario
- Issue #244 — Cloud validation run
