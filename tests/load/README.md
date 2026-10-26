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
| `scripts/throughput.js` | Roadmap headline metric: a constant start rate of a no-wait process (`load-linear-10` or `load-one-task`) | [§ throughput](#throughput-completed-instances-per-second-and-p99) |
| `scripts/throughput-ceiling.sh` | `throughput.js` at several rates: completed/s, p50/p95/p99 duration, CPU per service, DB transactions and WAL per instance | [§ throughput](#throughput-completed-instances-per-second-and-p99) |
| `scripts/throughput-window.sql` | The read-model result of one `throughput.js` window | used by `throughput-ceiling.sh` |
| `scripts/db-counters.sh`, `scripts/top-statements.sql` | Per-server committed transactions / WAL bytes; the statements behind them (`pg_stat_statements`) | used by `throughput-ceiling.sh` / by hand |
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
| `fixtures/linear-10-workflow.bpmn` | `load-linear-10` | Roadmap scenario `linear-10`: 10 script tasks in a row, no waits |
| `fixtures/one-task-workflow.bpmn` | `load-one-task` | Zeebe's reference benchmark process: start → one external `e2e-probe` task (completes after 50 ms) → end |
| `fixtures/timers-workflow.bpmn` | `load-timers-00` … `load-timers-59` | Template for `timers-1m.js`: start → `timeDate` timer → end; the script fills in the process id and due time |

## Docker Compose Setup

`docker-compose.yml` is the load-testing stack: the [Road to 1.0.0 target architecture](https://nightbaker.github.io/fleans/roadmap/v1/#target-architecture) scaled down to one laptop (10 CPU / 12 GB Docker VM) — REST split from the silos, the journal sharded on Citus, the read model on a primary with a streaming replica, membership and reminders on their own server, Kafka streams, no Redis.

| Service | Replicas | CPU weight / memory limit (each) | What |
|---|---:|---|---|
| `nginx` | 1 | 1024 / 128 MB | `:80` → `fleans-api` (config: `nginx.conf` — sized for 2 000 VUs; nginx's defaults drop connections above ~250 VUs, which k6 reports as `EOF`) |
| `fleans-api` | 2 | 1024 / 512 MB | `Fleans.Api`, `Fleans:Role=Api` — REST only, an Orleans client with no grains (#913) |
| `fleans-core` | 3 | **2048** / 1 GB | `Fleans.Api`, `Fleans:Role=Core` — workflow grains |
| `fleans-projector` | 1 | 1024 / 768 MB | `Fleans.Api`, `Fleans:Role=Projector` — the read-model projector only (#917): `InstanceAdvanced` → journal tail → read-model write |
| `fleans-worker` | 1 | 512 / 512 MB | `Fleans.WorkerHost`, `Fleans:Role=Worker` — script / condition grains |
| `fleans-plugin` | 1 | 512 / 384 MB | `Fleans.E2E.PluginHost`, `Fleans:Role=Plugin` — external worker hosting the `e2e-probe` custom task |
| `kafka` | 1 | 1024 / 1.25 GB | Orleans streams (single broker, KRaft) |
| `citus-coord` | 1 | **2048** / 1.25 GB | Journal store, Citus coordinator (`Persistence:Journal:Provider=Citus`, #879): routes every journal command to its shard, holds none |
| `citus-w1`, `citus-w2` | 1 + 1 | **2048** / 1.25 GB | Citus workers: the journal and snapshot tables, hash-distributed by `InstanceId` into `FLEANS_JOURNAL_SHARDS` shards (default 32 here, 128 in production) — the write path |
| `citus-setup` | one-shot | — | Registers the coordinator and the workers; the silos' schema bootstrap then distributes the tables |
| `postgres` | 1 | **2048** / 1.25 GB | Read-model primary (written by the projector) and the other grain state; `pg_stat_statements` loaded |
| `postgres-replica` | 1 | 1024 / 768 MB | Streaming hot standby of `postgres`; `ConnectionStrings:fleans-query` points here, so every list / state read is off the primary |
| `postgres-ops` | 1 | 1024 / 768 MB | Orleans membership (`fleans-cluster`, #885) and reminders (`fleans-reminders`, #886) |

The Citus image is built from source (`citus/Dockerfile`, Citus 12.1 on `postgres:16`): `citusdata/citus` is published for amd64 only, and emulated on an arm64 laptop its coordinator was several times slower and was OOM-killed at 50 starts/s. On an amd64 host `FLEANS_CITUS_IMAGE=citusdata/citus:12.1` uses the upstream image.

Every process opens one Npgsql data source per connection string (`fleans`, `fleans-query`, `fleans-journal`), each pooling up to 100 connections by default (#910); the stack caps them with `Maximum Pool Size` 30 (10 on Worker) and sets `No Reset On Close=true`, which drops the `DISCARD ALL` Npgsql otherwise sends on every pooled-connection reuse (~7 per instance, each a server transaction). PgBouncer is not in the stack: in transaction mode it would break the session-level advisory lock that serializes the schema migrations at startup (`EnsureDatabaseSchemaAsync`), and the capped pools already bound the server connections at this size.

CPU is split by `cpu_shares` weights, not `cpus` caps: when the host is contended, Core, Citus and the read-model primary get 4× the CPU of Worker and Plugin, and otherwise everyone may use every core. A `cpus` cap also lowers .NET's `Environment.ProcessorCount` (Orleans scheduler, thread pool, server-GC heaps); in `long-diverse` step runs, `cpus: 2` per Core completed 8.3 instances/s with start p95 174 ms, no limits 9.5/s and 38 ms, weights 9.6/s and 27 ms. The memory limits add up to ~12 GB, but the measured peaks are about half (Core ~450 MB each), so a 12 GB Docker VM is enough.

### Build images

```bash
bash tests/load/build-images.sh        # fleans-api / fleans-worker / fleans-plugin (tag :loadtest, TAG=… to override) + fleans-citus:12.1
```

The script runs `dotnet publish` and builds each image on `mcr.microsoft.com/dotnet/aspnet:10.0`.

### Start

```bash
cd tests/load
docker compose up -d --wait
docker compose exec nginx nginx -s reload     # pick up all fleans-api replicas
EXPECTED_REPLICA_COUNT=2 bash verify-cluster.sh
```

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

### Throughput: completed instances per second and p99

The roadmap's headline metric ("How it is measured"): completed instances per second of a process with no waits, and its p99 duration. `throughput.js` only starts instances, at a constant arrival rate (an open model — a slow cluster does not slow the offered load down), and never polls. The result comes from the read model afterwards: `CompletedAt − CreatedAt` per instance, both the journal events' `OccurredAt`, so the client adds neither load nor polling error.

```bash
cd tests/load
RATES="50 75 100 150" PROCESS=load-linear-10 DURATION=1m bash scripts/throughput-ceiling.sh   # → results/throughput-<ts>/
RATES="50 100 150" PROCESS=load-one-task bash scripts/throughput-ceiling.sh
```

Per rate it runs a warm-up (`WARMUP`, default 30 s) and the measured window (`DURATION`), waits until the window's instances finished (`WAIT_S`), and writes `<process>-<rate>.{k6,sql,cpu,db}.txt` plus `<process>-summary.txt`; the first rate is preceded by an unmeasured pre-warm (`PREWARM`, default 60 s — the first minute after a cold start is several times slower). Knobs of `throughput.js` are `TP_*`, not `K6_*`: k6 reads `K6_DURATION` itself and would replace the scenarios with one VU. A rate counts as sustained when `completed_per_s` matches it, nothing is `not_finished`, and `http_req_failed` / `dropped_iterations` stay near zero.

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

While the due minute runs, sample the reminders database's CPU (`postgres-ops`, its own container):

```bash
while sleep 5; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' | grep postgres; done
```

About a minute after the teardown line's "due until" time, read the result:

```bash
docker compose exec -T postgres psql -U postgres -d fleans < scripts/timers-1m-lag.sql
# armed | fired | failed | not_fired | lag_p50_s | lag_p99_s | lag_max_s
docker compose exec -T postgres-ops psql -U postgres -d ops -c \
  "SELECT idx_scan, idx_tup_read FROM pg_stat_user_indexes WHERE indexrelname = 'ix_reminderstable_serviceid_grainhash'"
```

`lag_s` is `CompletedAt − due time` from the read model, so it also contains one start → end
transition (milliseconds). `idx_scan` growing every `RefreshReminderListPeriod` (5 min) confirms
the range reads use the `(ServiceId, GrainHash)` index. Knobs: `K6_TIMERS_COUNT` (default
1 000 000), `K6_TIMERS_RATE` (starts/s, default 2 000 — lower it if `http_req_failed` climbs),
`K6_TIMERS_SLACK_S` (default 120 s between arming end and `T0`; `timers_armed_late` counts starts
that missed it), `K6_TIMERS_MAX_VUS` (default 1 000). The stack has no Redis; to compare against Redis
reminders, add a `redis` service and set `Fleans__Reminders__Provider=Redis` plus
`ConnectionStrings__orleans-redis` on the silos.

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
- `results/target-arch-2026-10-24/report.md` — the Road to 1.0.0 target architecture: ceilings,
  overload, timers, the scaling matrix and a resource estimate.

Commit a run's `report.md` only. Per-run directories (`results/throughput-*`, `results/scaling-*`)
are git-ignored: they go stale with every engine change and add dozens of files per report. Link the
commit that produced them from the report if the raw numbers matter. Overrides a report needs to
reproduce its rows go in `overrides/`.

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
