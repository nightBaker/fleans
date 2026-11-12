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
| `scripts/signal-fanout.js` | Scenario `signal-fanout`: one signal to N waiting instances while linear-10 runs (deploys its own fixtures) | [§ signal-fanout](#6-signal-fanout-one-signal-to-many-waiting-instances) |
| `scripts/signal-fanout.sql` | Parked / received / waiting counts and linear-10 before vs during for a `signal-fanout` run | `psql … < scripts/signal-fanout.sql` |
| `scripts/long-lived.js` | Scenario `long-lived`: linear-10's start rate on an empty cluster vs with N instances waiting on a message (deploys its own fixtures) | [§ long-lived](#8-long-lived-many-waiting-instances-then-linear-10) |
| `scripts/long-lived.sql` | Parked / waiting counts and linear-10 starts, completions and duration p99 per window for a `long-lived` run | `psql … < scripts/long-lived.sql` |
| `scripts/mi-fanout.js` | Scenario `mi-fanout`: one instance forks N parallel branches that each wait for a message (deploys its own fixture) | [§ mi-fanout](#7-mi-fanout-many-waiting-branches-in-one-instance) |
| `scripts/mi-fanout.sql` | Journal batches, events and snapshot size of the instances of a `mi-fanout` run | `psql … < scripts/mi-fanout.sql` |
| `scripts/throughput.js` | Roadmap headline metric: a constant start rate of a no-wait process (`load-linear-10` or `load-one-task`) | [§ throughput](#throughput-completed-instances-per-second-and-p99) |
| `scripts/throughput-ceiling.sh` | `throughput.js` at several rates: completed/s, p50/p95/p99 duration, CPU per service, DB transactions and WAL per instance | [§ throughput](#throughput-completed-instances-per-second-and-p99) |
| `scripts/throughput-window.sql` | The read-model result of one `throughput.js` window | used by `throughput-ceiling.sh` |
| `scripts/db-counters.sh`, `scripts/top-statements.sql` | Per-server committed transactions / WAL bytes; the statements behind them (`pg_stat_statements`) | used by `throughput-ceiling.sh` / by hand |
| `scripts/throughput.sql` | Completed instances/s and p50 / p99 process duration per process key, from the read model | [§ Completed instances per second](#completed-instances-per-second-and-p99-process-duration) |
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
| `fixtures/signal-fanout-workflow.bpmn` | `load-signal-fanout` | start → wait for the `loadFanout` signal → end (`signal-fanout.js`) |
| `fixtures/long-lived-workflow.bpmn` | `load-long-lived` | start → wait for the `loadLongLived` message (correlation key `parkId`, never sent) → end (`long-lived.js`) |
| `fixtures/mi-fanout-workflow.bpmn` | `load-mi-fanout` | start → parallel multi-instance sub-process (start → wait for the item's `loadMiFanout` message → end) → end (`mi-fanout.js`) |

## Docker Compose Setup

`docker-compose.yml` is the load-testing stack: the [Road to 1.0.0 target architecture](https://nightbaker.github.io/fleans/roadmap/v1/#target-architecture) scaled down to one laptop (10 CPU / 12 GB Docker VM) — REST split from the silos, the journal sharded on Citus, the read model on a primary with a streaming replica, membership and reminders on their own server, Kafka streams, no Redis.

| Service | Replicas | CPU weight / memory limit (each) | What |
|---|---:|---|---|
| `nginx` | 1 | 1024 / 128 MB | `:80` → `fleans-api` (config: `nginx.conf` — sized for 2 000 VUs; nginx's defaults drop connections above ~250 VUs, which k6 reports as `EOF`) |
| `fleans-api` | 2 | 1024 / 512 MB | `Fleans.Api`, `Fleans:Role=Api` — REST only, an Orleans client with no grains (#913) |
| `fleans-core` | 3 | **2048** / 1 GB | `Fleans.Api`, `Fleans:Role=Core` — workflow grains |
| `fleans-worker` | 1 | 512 / 512 MB | `Fleans.WorkerHost`, `Fleans:Role=Worker` — script / condition grains |
| `external-worker` | 2 | 512 / 256 MB | `Fleans.E2E.ExternalWorker`, outside the cluster — serves the `e2e-probe` custom task over gRPC |
| `kafka` | 1 | 1024 / 1.25 GB | Orleans streams (single broker, KRaft) |
| `citus-coord` | 1 | **2048** / 1.25 GB | Journal store, Citus coordinator (`Persistence:Journal:Provider=Citus`, #879): routes every journal command to its shard, holds none |
| `citus-w1`, `citus-w2` | 1 + 1 | **2048** / 1.25 GB | Citus workers: the journal and snapshot tables, hash-distributed by `InstanceId` into `FLEANS_JOURNAL_SHARDS` shards (default 32 here, 128 in production) — the write path |
| `citus-setup` | one-shot | — | Registers the coordinator and the workers; the silos' schema bootstrap then distributes the tables |
| `postgres` | 1 | **2048** / 1.25 GB | Read-model primary (written by the projector) and the other grain state; `pg_stat_statements` loaded |
| `postgres-replica` | 1 | 1024 / 768 MB | Streaming hot standby of `postgres`; `ConnectionStrings:fleans-query` points here, so every list / state read is off the primary |
| `postgres-ops` | 1 | 1024 / 768 MB | Orleans membership (`fleans-cluster`, #885) and reminders (`fleans-reminders`, #886) |

The Citus image is built from source (`citus/Dockerfile`, Citus 12.1 on `postgres:16`): `citusdata/citus` is published for amd64 only, and emulated on an arm64 laptop its coordinator was several times slower and was OOM-killed at 50 starts/s. On an amd64 host `FLEANS_CITUS_IMAGE=citusdata/citus:12.1` uses the upstream image.

Every process opens one Npgsql data source per connection string (`fleans`, `fleans-query`, `fleans-journal`), each pooling up to 100 connections by default (#910); the stack caps them with `Maximum Pool Size` 30 (10 on Worker) and sets `No Reset On Close=true`, which drops the `DISCARD ALL` Npgsql otherwise sends on every pooled-connection reuse (~7 per instance, each a server transaction). PgBouncer is not in the stack: in transaction mode it would break the session-level advisory lock that serializes the schema migrations at startup (`EnsureDatabaseSchemaAsync`), and the capped pools already bound the server connections at this size.

CPU is split by `cpu_shares` weights, not `cpus` caps: when the host is contended, Core, Citus and the read-model primary get 4× the CPU of the Worker silo and task workers, and otherwise everyone may use every core. A `cpus` cap also lowers .NET's `Environment.ProcessorCount` (Orleans scheduler, thread pool, server-GC heaps); in `long-diverse` step runs, `cpus: 2` per Core completed 8.3 instances/s with start p95 174 ms, no limits 9.5/s and 38 ms, weights 9.6/s and 27 ms. The memory limits add up to ~12 GB, but the measured peaks are about half (Core ~450 MB each), so a 12 GB Docker VM is enough.

### Build images

```bash
bash tests/load/build-images.sh        # fleans-api / fleans-worker / fleans-external-worker (tag :loadtest, TAG=… to override) + fleans-citus:12.1
```

The script runs `dotnet publish` and builds each image on `mcr.microsoft.com/dotnet/aspnet:10.0`.

### Start

```bash
cd tests/load
docker compose up -d --wait
docker compose exec nginx nginx -s reload     # pick up all fleans-api replicas
EXPECTED_REPLICA_COUNT=2 bash verify-cluster.sh
```

`FLEANS_WORKER_IMAGE=<image>` swaps the task-worker image.

### Teardown

```bash
docker compose down -v
```

### Alternative: Aspire-generated 2-replica stack

`FLEANS_LOAD_TEST_MODE=true dotnet run --project src/Fleans/Fleans.Aspire -- --publisher docker-compose --output-path tests/load/generated`
emits nginx fronting 2 `fleans-core` replicas (gitignored `tests/load/generated/`). Released compose / helm
artifacts omit this fan-out. That stack has no task worker, so run `long-diverse.js` against it with
`K6_EXPECT_WORKER=false`.

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

The `externalWork` step is an `e2e-probe` custom task, so the target cluster needs a connected worker
(`docker-compose.yml` has two). Against a cluster without one set `K6_EXPECT_WORKER=false` — the step then
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

#### `load-one-task`: gRPC workers against plugin silos (#1013)

Custom tasks run on gRPC workers only since #1013. Before the Plugin role was removed, `load-one-task` ran on both on the same laptop stack (Docker VM ~7.75 GB, 1-minute windows, 50 ms of task work):

| starts/s | plugin silo: failed, p50 / p99 (s) | 2 gRPC workers: failed, p50 / p99 (s) |
|---|---|---|
| 300 | 0, 0.135 / 0.210 | 0, 0.137 / 0.212 |
| 400 | 0, 0.136 / 0.211 | 0, 0.138 / 0.213 |
| 500 | 0, 0.139 / 0.217 | 0, 0.142 / 0.238 |

The same stack on the final #1013 code, with no plugin silo in the cluster, gave 0 failed and p50 0.139 / p99 0.216 at 500/s. Neither mode saturated, so 500/s is a floor, not a ceiling. Total CPU at 500/s was about 9 % higher with gRPC (900 % against 824 %), mostly on Core and the API, and the two workers used ~46 MB each against the plugin silo's ~220 MB. Other containers on the Docker host skew a run badly: check `<process>-<rate>.cpu.txt` for names outside `fleans-load`.

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

Publish the numbers (hardware, counts, lag p50/p99/max, reminders-Postgres CPU) in
[fleans-load-results](https://github.com/nightBaker/fleans-load-results) and on the website's load-testing page.

### 6. `signal-fanout`: one signal to many waiting instances

The roadmap's broadcast scenario (#976): park `K6_FANOUT_COUNT` instances on one signal catch
(default 100 000), broadcast the signal once, and run linear-10 at `K6_LINEAR_RATE`/s in a window
before and a window from the broadcast on. **Pass:** every parked instance receives the signal, and
linear-10 keeps its start rate and latency during the broadcast.

The read-model projector runs on the worker, one per projector bucket (`Fleans:Projector:BucketCount`,
64, #1011), so neither the parked instances nor the completion burst add projectors: the default
512 MB worker runs 100 000. `FLEANS_SIGNAL_DELIVERY_CONCURRENCY` sets
`Fleans:Signals:DeliveryConcurrency` on the silos (default 1).

```bash
cd tests/load
docker compose down -v
docker compose up -d --wait
docker compose exec nginx nginx -s reload
K6_TARGET_URL=http://localhost:80 K6_FANOUT_COUNT=20000 K6_FANOUT_RATE=150 K6_WINDOW_S=60 k6 run scripts/signal-fanout.js
# a minute later, once the projector has caught up:
docker compose exec -T postgres psql -U postgres -d fleans < scripts/signal-fanout.sql
# parked | received | failed | waiting | first_received | last_received | delivery_spread_s
# window | started | completed | duration_p99_s        (linear-10, before / during the broadcast)
```

k6 prints the broadcast's answer time and compares linear-10's start latency per window
(`workflow_start_duration{scenario:linear_before|linear_during}`). Keep `K6_FANOUT_RATE` low enough
that the stack does not answer starts with 429 (150/s on a laptop): only accepted starts are parked.
Knobs: `K6_FANOUT_RATE` (default 1 000), `K6_LINEAR_RATE` (default 20), `K6_WINDOW_S` (default 60),
`K6_SETTLE_S` (pause after parking, default 30), `K6_FANOUT_MAX_VUS` (default 1 000).

Laptop run, 2026-11-01 (10-CPU / 12 GB Docker VM, 2 GB worker, 20 000 parked, linear-10 at 20/s):

| Delivery | Broadcast answered | Received | Delivered within | linear-10 start p95 before → during | linear-10 duration p99 before → during |
|---|---:|---:|---:|---|---|
| Direct calls from the bucket, 20 at a time (before #976) | 19 s | 19 896 / 19 896 | 18 s | 22 ms → 1.5 s | 0.14 s → 9.6 s |
| Stream per bucket, `DeliveryConcurrency` 2 | 0.8 s | 19 900 / 19 900 | 29 s | 21 ms → 509 ms | 0.10 s → 3.1 s |
| Stream per bucket, `DeliveryConcurrency` 1 (default) | 0.6 s | 19 951 / 19 951 | 19 s | 19 ms → 163 ms | 0.08 s → 0.8 s |

linear-10 kept its start rate in every run (1 206 vs 1 196 starts in the two 60 s windows at
concurrency 1); fewer deliveries in flight were both faster and gentler, because they contend less
for the journal. A stream per *instance* (one event per subscriber) was tried first and dropped: the
pulling agents called thousands of instances at once and stalled the cluster.

At **100 000** (6 GB worker, same day) the broadcast answered in 5.9 s and every one of the 94 990
parked instances received the signal — counted in the journal (`WorkflowEventBatches` head
versions), because the read model fell minutes behind. linear-10 was **not** unaffected: 452 of
2 399 starts during the broadcast were answered 429 and its duration p99 rose from 0.74 s to 24 s.
The worker (read-model projector next to the script executor) sat at 500–730 % CPU while the cores
and Citus stayed at 15–30 %: projector capacity is the limit, tracked in #1011.

**#1011, 2026-11-12** (same laptop, 100 000 parked at 150/s, `DeliveryConcurrency` 1, linear-10 at
20/s; counts from `signal-fanout.sql`):

| Build | Worker | Parked / received | linear-10 starts before → during | linear-10 failed starts | linear-10 duration p99 before → during | Worker peak | Delivery spread |
|---|---|---:|---|---:|---|---:|---:|
| main @ `0db949fe` | 6 GB | 99 441 / 99 441 | 1 208 → 955 (−21 %) | 238 | 0.09 s → 14.5 s | 2.98 GB | 192 s |
| #1011 | 512 MB | 96 794 / 96 792 | 1 212 → 815 (−33 %) | 372 | 0.23 s → 120 s | 512 MB, out of memory in the burst | 252 s |
| #1011 | 2 GB | 97 251 / 97 251 | 1 213 → 1 186 (−2 %) | 2 | 0.15 s → 6.3 s | 1.25 GB | 372 s |
| #1011, projectors never idle (`IdleDeactivation` 30 min) | 6 GB | 98 815 / 98 815 | 1 218 → 634 (−48 %) | 547 | 1.4 s → 23 s | 1.98 GB | 826 s |
| #1011, bucket projectors (64) @ `3793b3a8` | 512 MB | 99 980 / 99 980 | 1 210 → 1 190 (−2 %) | 0 | 0.20 s → 0.50 s | 397 MB | 103 s |

- On main, the worker's CPU during the burst went mostly to journal reads failing: every
  notification started a journal read at once, far more than the worker's journal pool holds (10).
  Each read waited for a connection, failed after the pool timeout, was logged in full by EF on the
  console and was redelivered. `Fleans:Projector:MaxConcurrentReplays` (default 8) now queues those
  reads in the projector instead. The worker also logs at `Warning`, like the API host; it used to
  log every EF command at `Information`.
- Idle projectors leaving memory is better on both counts: last row against the 2 GB one.
- Up to the idle-projector rows, linear-10's duration p99 during the burst stayed in seconds (6.3 s
  at best), and the core silos logged tens of thousands of `Message expired before placement could
  complete for grain workflowinstanceprojector` (Orleans 101038 / 100071): with one stream per
  instance the pulling agents addressed a projector for every completed instance at once, and
  placement on the worker could not keep up.
- **Bucket projectors** (last row): `InstanceAdvanced` goes to the stream of one of 64 buckets, and
  one projector per bucket serves its instances — the #976 move for `SignalDeliveryPage`. No
  placement expiry was logged, linear-10's p99 stayed at 0.5 s with no failed start, and the
  default 512 MB worker peaked at 397 MB and 123 % CPU. The read model was caught up 165 s after
  the broadcast (delivery itself took 103 s), so 64 serial buckets kept pace with the burst.

### 7. `mi-fanout`: many waiting branches in one instance

One instance forks `K6_MI_SIZES` parallel multi-instance branches (one instance per size, one at a time), and each branch waits for its own message (#861). Every subscription persists before its external call, all in the start's turn, so N branches mean N appends in one grain call. The call is the start: **pass** is a 200 within the 30 s grain-call timeout with every branch waiting, and completion once every message is delivered.

```bash
cd tests/load
K6_TARGET_URL=http://localhost:80 K6_MI_SIZES=1000,2000,3000 k6 run scripts/mi-fanout.js
# with the instance ids the script printed:
docker compose exec -T citus-coord psql -U postgres -d journal -v ids="'{id1,id2}'" < scripts/mi-fanout.sql
# InstanceId | batches | events | journal_bytes | snapshot_bytes
```

Knobs: `K6_MI_SIZES` (default `100,500,1000,2000`), `K6_MI_DELIVERY_CONCURRENCY` (default 50), `K6_MI_COMPLETE_TIMEOUT_S` (default 600).

Laptop run, 2026-11-06 (10-CPU / 12 GB Docker VM, default stack, nothing else running, journal on Citus):

| Branches | Start answered | All messages delivered in | Journal: batches / events | Last snapshot |
|---:|---:|---:|---|---:|
| 100 | 2.6 s (first start after deploy) | 1.0 s | | |
| 500 | 5.9 s | 4.8 s | | |
| 1 000 | 7.1–7.6 s | 18–25 s | 2 000 / 18 015 | 0.76 MB |
| 2 000 | 12.3 s | 135 s | 4 000 / 36 015 | 1.5 MB |
| 3 000 | 25.7 s | 445 s | 6 000 / 54 015 | 2.1 MB |
| 5 000 | **500 after 30 s** | not run | 5 000 / 55 009 | 3.3 MB |
| 8 000 | **500 after 30 s** | not run | 8 000 / 88 009 | 5.2 MB |

- **The timeout is reached between 3 000 and 5 000 branches.** A 1 000-item fan-out stays well inside it, so subscriptions are not batched: persisting each one before its external call is simple and safe, and it stays inside the timeout at that size.
- **A start that times out keeps running.** The 5 000 and 8 000 instances went on to subscribe every branch, but the caller got a 500 and no instance id, and a retry would start a second instance.
- **Delivery grows about cubically,** and is the larger cost: each message is one turn of the instance (about 20 ms at 1 000 branches, 148 ms at 3 000), and those turns rescan state. `CompleteFinishedSubProcessScopes` calls `GetEntriesInScope`, a scan of all entries, per active scope host: about 60 ms per call at 2 000–3 000 branches, measured alone. It explains a large share, not necessarily all of it.
- Each instance's first batch over `MaxEventsPerLoad` logs one 4072 on the projector, whose replay finds no snapshot yet. The stream redelivers it, and the read model caught up in every run.

### 8. `long-lived`: many waiting instances, then linear-10

The roadmap's waiting-instances scenario (#973): measure linear-10's start rate on an empty cluster,
park `K6_PARK_COUNT` instances on a message catch that never fires (default 10 000 000, each with
its own `parkId` correlation key), then measure the start rate again with all of them waiting.
**Pass:** the start rate with the parked instances drops by no more than 10 %, and none of them is
lost (all still waiting).

The start rate is a closed model: `K6_LINEAR_VUS` VUs start linear-10 back to back for
`K6_WINDOW_S` in each window, so accepted starts per window is the rate the cluster sustains. A
`K6_WARMUP_S` warm-up runs first under its own key (`load-linear-10-warmup`), so a cold cluster does
not lower the empty window. At saturation the admission limiter (`Fleans:Admission`) answers some
starts 429: those count as `linear_rejected`, not as failed requests, and only accepted starts are
compared. The k6 summary ends with one line: starts/s empty → loaded, the drop, PASS / FAIL and the
429s per window.

As in `signal-fanout`, the read-model projector keeps one activation per waiting instance on the
worker (#1011): start the stack with `overrides/signal-fanout.yml` (2 GB worker).

```bash
cd tests/load
docker compose down -v
docker compose -f docker-compose.yml -f overrides/signal-fanout.yml up -d --wait
docker compose exec nginx nginx -s reload
K6_TARGET_URL=http://localhost:80 K6_PARK_COUNT=20000 K6_PARK_RATE=150 k6 run scripts/long-lived.js
# 30 s later, once linear-10 has drained:
docker compose exec -T postgres psql -U postgres -d fleans < scripts/long-lived.sql
# parked | waiting | failed | first_parked | last_parked
# phase | started | completed | completed_per_s | duration_p99_s     (linear-10, empty / loaded)
```

Keep `K6_PARK_RATE` low enough that parking is not answered 429 (150/s on a laptop): only accepted
starts are parked. At the default 1 000/s, 10 M takes about 2 h 47 min. Knobs: `K6_PARK_RATE`
(default 1 000), `K6_LINEAR_VUS` (default 20), `K6_WINDOW_S` (default 120), `K6_WARMUP_S` (default
60), `K6_SETTLE_S` (pause between the phases, default 30), `K6_PARK_MAX_VUS` (default 1 000).

Laptop runs, 2026-11-12 (10-CPU / 12 GB Docker VM, 2 GB worker, 20 000 requested, linear-10 with 20
VUs, two fresh stacks with the same settings):

| Run | Parked (all still waiting) | linear-10 starts/s empty → loaded | Drop | 429 empty / loaded | Start avg empty → loaded | Duration p99 empty → loaded |
|---|---:|---|---:|---|---|---|
| 1 | 19 600 | 227.7 → 204.1 | 10.3 % | 0 / 4 243 | 88 ms → 83 ms | 1.75 s → 2.12 s |
| 2 | 19 957 | 267.3 → 161.0 | 39.8 % | 0 / 0 | 75 ms → 124 ms | 0.73 s → 1.26 s |

**Not a pass, and not a measured effect of the waiting instances either.** Every parked instance
stayed waiting and none failed. The drop sits inside the stack's own window-to-window spread:
a control run on a fresh stack with the same windows and **no** parked instances (2026-11-12, same
laptop) went 130 → 62 → 104 → 82 → 88 → 129 starts/s. In every window, with or without parked
instances, the start rate tracked the journal append latency on the Citus coordinator
(`INSERT INTO "WorkflowEventBatches"`, `pg_stat_statements`): ~14 appends are in flight at any
time, 11 per linear-10 instance, so starts/s ≈ 14 / (11 × append latency) — 9.6 ms → 130/s,
18.6 ms → 62/s; the loaded window of run 2 was 10.0 ms → 118/s. The append latency moved with CPU
contention on the host (the Docker VM uses all 10 cores, shared with everything else on the
laptop), not with the parked instances; idle collection and stream inactivity (2–15 min) made no
difference either. A laptop run cannot resolve a 10 % gate: run it on dedicated hosts (#974), and
read the append latency next to the start rate.

## Completed instances per second and p99 process duration

The Road to 1.0.0 reports **completed instances per second** and **p99 process duration**, not
the HTTP start rate or `/Execution/start` latency that k6 measures (`http_reqs`,
`workflow_start_duration`). Two ways to read them:

**From the read model — works with this compose stack as is.** On a fresh stack
(`docker compose down -v` before the run), once the run has drained:

```bash
docker compose exec -T postgres psql -U postgres -d fleans < scripts/throughput.sql
# process_key | completed | failed | completed_per_s | peak_10s_per_s | p50_s | p99_s | max_s
```

`completed_per_s` spans the first to the last completion (ramps included); `peak_10s_per_s` is
the busiest 10-second window — read it as the sustained rate. Durations are
`CompletedAt − CreatedAt`, the journaled start and completion times, so projection lag does not
skew them.

**From the `Fleans` meter — live, during the run.** The compose stack runs no metrics pipeline:
the silos export OpenTelemetry only over OTLP, when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (add it
to `x-silo-env` and run a collector — see the website's *Configuring observability* guide; the
`grafana/` provisioning expects a Prometheus at `http://prometheus:9090`). Then:

```promql
sum(rate(fleans_workflow_terminated_total{result="completed"}[1m]))                                           # completed/s
histogram_quantile(0.99, sum by (le) (rate(fleans_workflow_duration_seconds_bucket{result="completed"}[5m]))) # p99 duration, s
```

The same meter carries the other scale metrics — `fleans.journal.append.duration`,
`fleans.projection.lag`, `fleans.timer.lag`, `fleans.kafka.consumer.lag` (catalog:
`website/src/content/docs/reference/observability.md`).

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

Reports live in a separate repo, [nightBaker/fleans-load-results](https://github.com/nightBaker/fleans-load-results) — its README lists the runs
and how to add one. The scripts here write to `results/`, which is git-ignored: copy a finished run
there with its `report.md`. Overrides a report needs to reproduce its rows stay here in `overrides/`.

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
- Issue #973 — `long-lived` and `signal-fanout` scenarios
- Issues #902, #903 — regressions found by the 2026-10-23 run (late timers, lower start throughput)
