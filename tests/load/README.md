# Fleans Load Tests

k6 load test suite for the Fleans workflow engine.

## Prerequisites

- [k6](https://k6.io/docs/get-started/installation/), Docker, the .NET SDK from `global.json`
- Fleans cluster running (see [Docker Compose setup](#docker-compose-setup) below)
- BPMN fixtures deployed (run `setup.js` first)

## Scripts

| Script | Purpose | Invocation |
|--------|---------|---------|
| `scripts/setup.js` | Deploy BPMN fixtures | `k6 run scripts/setup.js` (run once) |
| `scripts/linear.js` | Scenario 1: linear throughput | `k6 run scripts/linear.js` |
| `scripts/parallel.js` | Scenario 2: parallel branching (3-branch fork/join) | `k6 run scripts/parallel.js` |
| `scripts/events.js` | Scenario 3 — event-driven with message correlation | `k6 run scripts/events.js` |
| `scripts/mixed.js` | Mixed workload combining all scenarios | `k6 run scripts/mixed.js` |
| `scripts/long-diverse.js` | Scenario 5: long, diverse process with 4 client interactions + external task | `k6 run scripts/long-diverse.js` |
| `scripts/timers-1m.js` | Scenario `timers-1m`: 1 M durable timers due within one minute (deploys its own fixtures) | [§ timers-1m](#5-timers-1m-a-million-timers-due-in-one-minute) |
| `scripts/timers-1m-lag.sql` | Armed / fired counts and timer lag p50 / p99 for a `timers-1m` run | `psql … < scripts/timers-1m-lag.sql` |
| `scripts/metrics.js` | Shared metric definitions | Imported by scenario scripts |

**`setup.js`** (from #239) is a standalone file you run once via `k6 run setup.js` to deploy fixtures and verify the cluster is ready.

**`setup()`** exported from `events.js` is k6's per-run init hook — it verifies the `load-events` process is deployed and active before spawning VUs. Both are required but invoked at different times.

## Fixtures

| File | Process key | Description |
|------|------------|-------------|
| `fixtures/linear-workflow.bpmn` | `load-linear` | Simple start → script → end |
| `fixtures/parallel-workflow.bpmn` | `load-parallel` | Parallel gateway fork/join |
| `fixtures/events-workflow.bpmn` | `load-events` | Timer + message events |
| `fixtures/long-diverse-workflow.bpmn` | `load-long-diverse` | ~45 nodes: exclusive / parallel / inclusive / event-based gateways, embedded sub-process, parallel + sequential multi-instance, call activity, error + timer boundaries, timer + message catches, external `task`, `e2e-probe` custom task |
| `fixtures/long-diverse-child.bpmn` | `load-long-child` | Called by `load-long-diverse` (script → 1 s timer → script) |
| `fixtures/timers-workflow.bpmn` | `load-timers-00` … `load-timers-59` | Template for `timers-1m.js`: start → `timeDate` timer → end; the script fills in the process id and due time |

## Docker Compose Setup

`docker-compose.yml` is the load-testing stack — split roles on Kafka streaming, with the stores where the [1.0 roadmap](https://nightbaker.github.io/fleans/roadmap/v1/) puts them (journal, read model, reminders and membership on PostgreSQL):

| Service | Replicas | CPU weight / memory limit (each) | What |
|---|---:|---|---|
| `nginx` | 1 | 1024 / — | `:80` → `fleans-core` (config: `nginx.conf` — sized for 2 000 VUs; nginx's defaults drop connections above ~250 VUs, which k6 reports as `EOF`) |
| `fleans-core` | 3 | **2048** / 1.5 GB | `Fleans.Api`, `Fleans:Role=Core` — REST API, workflow grains |
| `fleans-worker` | 2 | 512 / 512 MB | `Fleans.WorkerHost`, `Fleans:Role=Worker` — script / condition grains |
| `fleans-plugin` | 1 | 512 / 384 MB | `Fleans.E2E.PluginHost`, `Fleans:Role=Plugin` — external worker hosting the `e2e-probe` custom task |
| `kafka` | 1 | 1024 / — | Orleans streams (single broker, KRaft) |
| `redis` | 1 | 1024 / — | Stream PubSubStore only — Fleans has no PostgreSQL provider for it yet (#911) |
| `postgres` | 1 | **2048** / 1.5 GB | Read model, grain state and the Orleans membership table (`Fleans:Clustering:Provider=Postgres`, #885; `FLEANS_CLUSTERING_PROVIDER=Redis` moves it back). `max_connections=300` for the silos' capped Npgsql pools |
| `postgres-journal` | 1 | **2048** / 2 GB | Workflow journal + snapshots (`ConnectionStrings:fleans-journal`, #879) — the write path, on its own server; `shared_buffers=512MB`, `max_wal_size=4GB` for the append-heavy journal |
| `postgres-reminders` | 1 | 1024 / — | Orleans reminders (`Fleans:Reminders:Provider=Postgres`, `ConnectionStrings:fleans-reminders`) — the production layout; `FLEANS_REMINDERS_PROVIDER=Redis` moves them back to Redis |

Every engine silo opens one Npgsql data source per connection string (`fleans`, `fleans-query`, `fleans-journal`), each pooling up to 100 connections by default; the stack caps them with `Maximum Pool Size` 30 on Core and 10 on Worker (#910) — the default pools ran out of connections at 400 VUs.

CPU is split by `cpu_shares` weights, not `cpus` caps: when the host is contended, Core and the two engine Postgres servers get 4× the CPU of Worker and Plugin, and otherwise everyone may use every core. A `cpus` cap also lowers .NET's `Environment.ProcessorCount` (Orleans scheduler, thread pool, server-GC heaps); in `long-diverse` step runs (laptop, 10 CPU / 12 GB Docker VM), `cpus: 2` per Core completed 8.3 instances/s with start p95 174 ms, no limits 9.5/s and 38 ms, these weights 9.6/s and 27 ms (journal Postgres peak 68 % of a core instead of 113 %, from the larger `shared_buffers`). The memory limits add up to ~9.4 GB, plus ~1.7 GB for Kafka and the unlimited services, so give Docker at least 12 GB.

Outside Aspire `FLEANS_LOAD_TEST_MODE=true` adds the Redis PubSubStore.

### Build images

```bash
bash tests/load/build-images.sh        # fleans-api / fleans-worker / fleans-plugin, tag :loadtest (TAG=… to override)
```

The script runs `dotnet publish` and builds each image on `mcr.microsoft.com/dotnet/aspnet:10.0`.

### Start

```bash
cd tests/load
docker compose up -d --wait
docker compose exec nginx nginx -s reload     # pick up all fleans-core replicas
EXPECTED_REPLICA_COUNT=3 bash verify-cluster.sh
```

`FLEANS_STREAMING_PROVIDER=Redis docker compose up -d --wait` runs the same topology on Redis streams.
`FLEANS_PLUGIN_IMAGE=<image>` swaps the plugin-host image.

### Teardown

```bash
docker compose down -v
```

### Alternative: Aspire-generated 2-replica stack

`FLEANS_LOAD_TEST_MODE=true dotnet run --project src/Fleans/Fleans.Aspire -- --publisher docker-compose --output-path tests/load/generated`
emits nginx fronting 2 `fleans-core` replicas (gitignored `tests/load/generated/`). Released compose / helm
artifacts omit this fan-out. That stack has no Plugin-role host, so run `long-diverse.js` against it with
`K6_EXPECT_PLUGIN=false`.

## Running

### 1. Deploy fixtures (required once per cluster restart)

```bash
K6_TARGET_URL=http://localhost:80 k6 run tests/load/scripts/setup.js     # docker-compose stack
k6 run --insecure-skip-tls-verify tests/load/scripts/setup.js           # Aspire dev stack (https://localhost:7140)
```

Override the target URL with `K6_TARGET_URL`:

```bash
K6_TARGET_URL=https://my-cluster.example.com k6 run tests/load/scripts/setup.js
```

### 2. Main scenario: long, diverse process (`long-diverse.js`)

Per iteration: start → wait for `waitApproval` → message `loadApproval` → wait for `manualReview` →
`POST /Execution/complete-activity` → wait for `waitConfirm` → message `loadConfirm` → wait for completion,
then checks that every branch taken matches the start variables (`amount`, `divisor`). ~10 % of instances
take the error-boundary path.

```bash
K6_TARGET_URL=http://localhost:80 K6_PROFILE=smoke k6 run tests/load/scripts/long-diverse.js   # 2 VUs × 5
K6_TARGET_URL=http://localhost:80 k6 run tests/load/scripts/long-diverse.js                    # step: 10→25→50→100 VUs
K6_TARGET_URL=http://localhost:80 K6_PROFILE=constant K6_VUS=50 K6_DURATION=5m k6 run tests/load/scripts/long-diverse.js
```

The `externalWork` step is an `e2e-probe` custom task, so the target cluster needs a Plugin-role host
(`docker-compose.yml` has one). Against a cluster without one set `K6_EXPECT_PLUGIN=false` — the step then
never completes, so use it only to measure the stages before the parallel join. Extra knobs:
`K6_STAGE_BUDGET_MS` (poll budget per stage, default 45 000), `K6_SUMMARY_PATH` (write the k6 JSON summary).

### 3. Run linear throughput scenario

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

### 4. Run parallel branching scenario

**Cloud / Docker Compose cluster (full load):**
```bash
k6 run --insecure-skip-tls-verify tests/load/scripts/parallel.js
```

**Local connectivity check (safe for dev):**
```bash
k6 run --vus 5 --iterations 20 --insecure-skip-tls-verify tests/load/scripts/parallel.js
```

`parallel.js` shares the same VU profile and `workflow_start_duration` metric as `linear.js`, so the two scripts can be compared directly. The fixture is a 3-branch fork/join (`branchA`/`branchB`/`branchC`); fork/join completion timing is asynchronous in Orleans and is **not** captured by `workflow_start_duration` — that metric records `POST /Execution/start` HTTP latency only. End-to-end fork/join latency is covered by issue #244.

### 5. `timers-1m`: a million timers due in one minute

Decides whether Orleans reminders on PostgreSQL carry a million pending BPMN timers, or whether
Fleans needs the sharded timer scheduler (roadmap "Timers", step 2; issue #886). **Pass:** timer
lag p99 < 2 s and no timer lost. A fail → open a follow-up issue for the scheduler shards.

The script picks a due minute `T0 … T0+59 s` in the future, deploys 60 copies of
`fixtures/timers-workflow.bpmn` (`load-timers-00` … `-59`, one per second of that minute), then
arms `K6_TIMERS_COUNT` instances at `K6_TIMERS_RATE` starts/s, round-robin over the keys. Each
instance parks on one durable timer — one `TimerCallbackGrain` with one Orleans reminder. k6 exits
after arming; the timers fire `K6_TIMERS_SLACK_S` later.

```bash
cd tests/load
docker compose down -v && docker compose up -d --wait     # FRESH stack: the lag query reads every load-timers-* instance
docker compose exec nginx nginx -s reload
K6_TARGET_URL=http://localhost:80 K6_TIMERS_COUNT=10000 K6_TIMERS_RATE=200 k6 run scripts/timers-1m.js   # smoke first
docker compose down -v && docker compose up -d --wait && docker compose exec nginx nginx -s reload
K6_TARGET_URL=http://localhost:80 k6 run scripts/timers-1m.js                                            # 1 M at 2 000/s ≈ 8.5 min
```

While the due minute runs, sample the reminders database's CPU (it is its own container):

```bash
while sleep 5; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' | grep postgres; done
```

About a minute after the teardown line's "due until" time, read the result:

```bash
docker compose exec -T postgres psql -U postgres -d fleans < scripts/timers-1m-lag.sql
# armed | fired | failed | not_fired | lag_p50_s | lag_p99_s | lag_max_s
docker compose exec -T postgres-reminders psql -U postgres -d reminders -c \
  "SELECT idx_scan, idx_tup_read FROM pg_stat_user_indexes WHERE indexrelname = 'ix_reminderstable_serviceid_grainhash'"
```

`lag_s` is `CompletedAt − due time` from the read model, so it also contains one start → end
transition (milliseconds). `idx_scan` growing every `RefreshReminderListPeriod` (5 min) confirms
the range reads use the `(ServiceId, GrainHash)` index. Knobs: `K6_TIMERS_COUNT` (default
1 000 000), `K6_TIMERS_RATE` (starts/s, default 2 000 — lower it if `http_req_failed` climbs),
`K6_TIMERS_SLACK_S` (default 120 s between arming end and `T0`; `timers_armed_late` counts starts
that missed it), `K6_TIMERS_MAX_VUS` (default 1 000). Compare against Redis reminders with
`FLEANS_REMINDERS_PROVIDER=Redis docker compose up -d --wait`.

Publish the numbers (hardware, counts, lag p50/p99/max, reminders-Postgres CPU) under `results/`
and on the website's load-testing page.

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
| `workflow_start_duration` | Trend | `p(95) < 2 000 ms` | HTTP duration of `POST /Execution/start` |
| `poll_until_catch_duration` | Trend | `p(95) < 2 500 ms` | Wall-clock from start to poll success |
| `message_accept_duration` | Trend | `p(95) < 2 000 ms` | HTTP duration of final `POST /Execution/message` |
| `message_retry_attempts` | Trend | *(none)* | Diagnostic only |
| `poll_stalls` | Rate | `rate < 1%` | Poll budget expired |
| `correlation_miss` | Rate | `rate < 1%` | Message retry budget expired |

## Rate-limit policy: `polling`

The `GET /Instances/{id}/state` endpoint uses `[EnableRateLimiting("polling")]`. This attribute is **opt-in** via the `RateLimiting:Polling` config section:

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

## Results

- `results/local-2026-10-23/report.md` — `main` before vs after the first Road-to-1.0 PRs (#888–#898):
  `long-diverse`, `linear-saturation` and `timers`, with the raw k6 summaries.

## Related issues

- Issue #237 — Docker Compose infrastructure setup
- Issue #238 — BPMN fixtures
- Issue #239 — This script: setup + linear scenario
- Issue #240 — Parallel branching scenario
- Issue #241 — Event-driven scenario
- Issue #242 — Mixed workload scenario
- Issue #244 — Cloud validation run
- Issues #866, #867, #868, #869 (and #846, #857) — findings from the `long-diverse` run on the split-roles Kafka stack
- Issue #886 — `timers-1m`, reminders on their own Postgres
- Issues #902, #903 — regressions found by the 2026-10-23 run (late timers, lower start throughput)
