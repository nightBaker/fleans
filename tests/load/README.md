# Fleans Load Testing Infrastructure

Docker Compose stack for multi-silo load testing:

```
k6 / curl → nginx:80 → [round-robin] → silo-1:8080
                                      → silo-2:8080
                              │
                       postgres:5432
                       redis:6379
```

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) ≥ v24
- [k6](https://k6.io/docs/get-started/installation/) ≥ v0.51 (for running load scenarios)

## Build

Run from the `tests/load/` directory:

```bash
docker compose build
```

The build context is `src/Fleans/` — Docker resolves all project-to-project references during `dotnet restore`.

## Run

```bash
docker compose up -d
```

Wait for all 5 services to become healthy (~60s on first run):

```bash
docker compose ps
```

All services should show `healthy`. nginx becomes healthy only after both silos are healthy.

## Verify

Send a request through the nginx load balancer:

```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:80/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"nonexistent"}'
```

Expected: `400` (no workflow deployed — confirms nginx → silo routing works end-to-end).

## Deploy BPMN Fixtures

Upload a BPMN fixture before running load scenarios:

```bash
curl -X POST http://localhost:80/Workflow/upload-bpmn \
  -F "file=@path/to/fixture.bpmn"
```

## Run k6 Scenarios

From the `tests/load/` directory:

```bash
k6 run scripts/setup.js          # deploy fixtures
k6 run scripts/mixed-workload.js # main load scenario
```

## Scripts & hooks

| Script | Purpose | Invocation |
|---|---|---|
| `setup.js` | One-time cluster bootstrap — deploys fixtures | `k6 run scripts/setup.js` |
| `events.js` | Scenario 3 — event-driven with message correlation | `k6 run scripts/events.js` |
| `mixed.js` | Mixed workload combining all scenarios | `k6 run scripts/mixed.js` |

**`setup.js`** (from #239) is a standalone file you run once via `k6 run setup.js` to deploy fixtures and verify the cluster is ready.

**`setup()`** exported from `events.js` is k6's per-run init hook — it verifies the `load-events` process is deployed and active before spawning VUs. Both are required but invoked at different times.

## Orleans Dashboard

The Orleans dashboard is available on each silo directly (not proxied through nginx):

- silo-1: `http://localhost:8081` (map port 8080 of silo-1 to 8081 if needed via `ports:` in compose)

## Tear Down

```bash
docker compose down -v
```

The `-v` flag removes the postgres data volume for a clean slate on the next run.

## Configuration

| Environment Variable | Default | Description |
|---|---|---|
| `FLEANS_STANDALONE` | `true` | Activates standalone Orleans config (clustering + storage outside Aspire) |
| `ConnectionStrings__orleans-redis` | `redis:6379` | Redis connection for Orleans clustering and PubSubStore |
| `ConnectionStrings__fleans` | postgres connection | PostgreSQL connection string |
| `Persistence__Provider` | `Postgres` | Persistence provider (always Postgres in this stack) |

PostgreSQL is tuned for write-heavy load testing (`synchronous_commit=off`, `shared_buffers=256MB`). Do not use these settings in production.

## k6 Environment Variables

| Variable | Default | Description |
|---|---|---|
| `K6_TARGET_URL` | `https://localhost:7140` | Base URL of the Fleans API |
| `K6_POLL_INTERVAL_MS` | `100` | Initial poll interval (ms) |
| `K6_POLL_MAX_ATTEMPTS` | `20` | Safety ceiling for poll iterations |
| `K6_POLL_BACKOFF_CAP_MS` | `500` | Max backoff per poll sleep |
| `K6_POLL_TOTAL_BUDGET_MS` | `3000` | Wall-clock budget for the entire poll phase |
| `K6_MESSAGE_RETRY_BUDGET_MS` | `1000` | Wall-clock budget for message delivery retries on 404 |
| `K6_MESSAGE_RETRY_INTERVAL_MS` | `150` | Sleep between message retry attempts. Starting heuristic — re-tune via this env var after #244's Cloud run publishes grain-commit latencies. |

All env vars must be unset or set to a positive integer. An empty string (e.g., `K6_POLL_INTERVAL_MS=`) falls back to the default.

## Metrics

| Metric | Type | Threshold | Notes |
|---|---|---|---|
| `workflow_start_duration` | Trend | `p(95)<2000` | HTTP duration of `POST /Workflow/start` |
| `poll_until_catch_duration` | Trend | `p(95)<2500` | Wall-clock from start to poll success |
| `message_accept_duration` | Trend | `p(95)<2000` | HTTP duration of the final `POST /Workflow/message` attempt (200 or final 404) |
| `message_retry_attempts` | Trend | *(none)* | Number of message delivery attempts (1 = first-try success). Diagnostic only. |
| `poll_stalls` | Rate | `rate<0.01` | Rate of iterations where poll budget expired without catching `waitMessage`. Denominator: all iterations. |
| `correlation_miss` | Rate | `rate<0.01` | Rate of iterations where message retry budget expired (all attempts 404). Denominator: iterations where poll succeeded (message was sent). Compare cautiously — `poll_stalls` and `correlation_miss` have different denominators. |

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
