# Target architecture on a laptop — 2026-10-24

The [Road to 1.0.0](https://nightbaker.github.io/fleans/roadmap/v1/#target-architecture) target
architecture, scaled down to one laptop (`tests/load/docker-compose.yml`), measured with the
roadmap's headline metric — completed instances per second and p99 process duration — and turned
into a resource estimate for Zeebe's published numbers.

## Summary

- **Latency target met on the laptop.** `linear-10` (10 script tasks, no waits) at 100 instances/s:
  p50 25 ms, **p99 73 ms**; at 150/s p99 0.40 s. `main` at 100/s: p50 1.47 s, p99 1.57 s — every
  script step waited ~100 ms for a stream poll.
- **Ceiling = the host.** 150 `linear-10`/s and 200 `one-task`/s (Zeebe's reference process) are
  sustained with p99 under 1 s; past that the 10 cores are at 100 % (≈ 45 % of it `sys` time of the
  Docker VM), not one component.
- **Per-instance cost** on the laptop: **36.6 ms of CPU per `linear-10` instance**, **22.2 ms per
  `one-task` instance**, all tiers included (REST, silos, journal, read model, Kafka).
- **Zeebe comparison** (details in [§ Resource estimate](#resource-estimate-for-zeebes-numbers)):
  - Zeebe's 2023 target, **500 PI/s at p99 < 1 s, ran on 8 brokers × 8 vCPU = 64 vCPU** (brokers
    only). Fleans needs an estimated **~50 vCPU** for 500 `linear-10`/s and **~32 vCPU** for 500
    `one-task`/s, with high availability and 35 % headroom, everything included.
  - On Zeebe's weekly benchmark process (1 task, 210 PI/s on 3 × 1.35 CPU brokers ≈ 4 CPU),
    Fleans' engine tiers need ≈ 8 vCPU at the same 100 % load — about **2× Zeebe's broker CPU per
    instance**, but Fleans' number includes its read model (Zeebe's Operate / Elasticsearch is not
    in Zeebe's figure).
  - The roadmap's level 1 (1 000 `linear-10`/s) needs ~100 vCPU; level 2 (10 000/s) ~950 vCPU, where
    the single read-model primary (40 k transactions/s) becomes the limit before CPU does.
- **Overload stops the cluster** past the ceiling, and it does not recover; Orleans load shedding
  does not prevent that, a REST rate limit at ~75 % of the ceiling does (§ Overload).
- **Three hot-path costs found and fixed by this run**: the per-activation membership-table read in
  the placement director (#919), a warning log line per activity (#919, #868), and Npgsql's
  `DISCARD ALL` per pooled-connection reuse (stack config: `No Reset On Close`).

## Setup

**Host:** MacBook Pro, Apple M1 Pro (8 performance + 2 efficiency cores), 16 GB; Docker Desktop
29.2 with 10 CPU / 12 GB. k6 v0.54 on the same host.

**Stack** (`tests/load/docker-compose.yml` on `feature/load-target-arch`):

| Tier | Services |
|---|---|
| REST | nginx → 2 × `fleans-api` (`Fleans:Role=Api`, an Orleans client, #913) |
| Orleans | 3 × `fleans-core`, 1 × `fleans-projector` (#917), 1 × `fleans-worker`, 1 × `fleans-plugin` (`e2e-probe` external task) |
| Journal | Citus 12.1 (built from source for arm64): coordinator + 2 workers, 32 shards by `InstanceId` |
| Read model | Postgres 16 primary + streaming hot standby; `ConnectionStrings:fleans-query` → standby |
| Ops | Postgres 16: Orleans membership + reminders |
| Streams | Kafka 7.9 (KRaft, 1 broker), implicit subscriptions only; no Redis anywhere |

CPU is shared by `cpu_shares` weights (no caps). Npgsql pools are capped (30 / 10) with
`No Reset On Close=true`.

**Builds:**

| Name | Code |
|---|---|
| `main` | `origin/main` at `db9e1696` (REST on the Core silos, Redis for the health check, no projector role — `overrides/main-override.yml`, built with `TAG=main`) |
| `target` | `main` + #908 (projection round trips) + #913 (Api role, optional Redis, load shedding) + #914 (cached start listeners) + #915 (signal buckets) + #916 (scale metrics) + #917 (projector role, replica reads) + #918 (in-process conditions, direct scripts) |
| `target + #919` | `target` + #919 (placement host cache, no warning per activity) |

**Method:** `scripts/throughput-ceiling.sh` — per rate a 20 s warm-up and a **1-minute** measured
window at a constant start rate (open model), after a 60 s unmeasured pre-warm; results from the
read model (`CompletedAt − CreatedAt`, both journal timestamps); CPU = `docker stats` average over
the window. The roadmap asks for 5 + 30 minutes — a laptop run is a shape, not a published number.
A rate counts as sustained when completions match it, nothing is left running and p99 < 1 s.
No other workload ran on the host (earlier runs alongside test suites gave 2–3× noisier numbers
and are not used).

## Results

### `linear-10` — 10 script tasks in a row

| offered /s | `main` p50 / p99 | `main` CPU | `target` p50 / p99 | `target` CPU | `target + #919` p50 / p99 | `target + #919` CPU |
|---:|---|---:|---|---:|---|---:|
| 25 | 1.30 s / 1.49 s | 2.0 | | | | |
| 50 | 1.39 s / 1.53 s | 3.0 | 24 ms / 334 ms | 2.7 | 23 ms / 68 ms | 2.7 |
| 75 | 1.44 s / 1.54 s | 4.0 | | | | |
| 100 | 1.47 s / 1.57 s | 5.0 | 31 ms / 255 ms | 5.4 | **25 ms / 73 ms** | 5.0 |
| 150 | — | — | 22 s / 39 s (not sustained) | 8.7 | **38 ms / 404 ms** | 6.5 |
| 200 | — | — | collapsed (see below) | 9.2 | — | — |

CPU in cores (sum over the stack's containers). `main` sustains 100/s too, but never under 1 s: its
p50 is the stream hop (~100 ms per script step × 10 + the start).

### `one-task` — Zeebe's benchmark process (start → one external task, 50 ms → end)

| offered /s | `main` p50 / p99 | `main` CPU | `target` p50 / p99 | `target` CPU | `target + #919` p50 / p99 | `target + #919` CPU |
|---:|---|---:|---|---:|---|---:|
| 50 | 137 / 212 ms | 2.0 | 137 / 212 ms | 1.9 | | |
| 100 | 139 / 217 ms | 3.2 | 138 / 213 ms | 3.1 | 139 / 216 ms | 2.8 |
| 150 | 146 / 227 ms | 4.7 | 139 / 216 ms | 4.3 | 140 / 224 ms | 4.2 |
| 200 | | | | | **145 / 264 ms** | 5.1 |
| 250 | | | | | overload (see below) | |

50 ms of the 140 ms p50 is the task itself; the other ~90 ms is the start, the custom-task event's
Kafka hop to the Plugin silo (stream poll) and the completion callback. Custom tasks keep the stream
(at-least-once delivery to plugin hosts), so this is the floor for an external task today.

### Where the CPU goes (`target + #919`)

From `scripts/capacity.py` (CPU = idle + rate × cost, fitted over the sustained rates):

| tier | `linear-10` ms / instance | `one-task` ms / instance |
|---|---:|---:|
| Core silos (workflow grains) | 15.5 | 8.2 |
| Journal (Citus coordinator + workers) | 8.6 | 3.7 |
| Projector silo | 4.0 | 2.7 |
| Read model (primary + standby) | 2.9 | 2.0 |
| Worker silo (scripts) | 2.3 | 0.5 |
| REST (Api role + nginx) | 1.9 | 1.6 |
| Kafka | 1.2 | 2.0 |
| Plugin (external task) | 0.1 | 1.5 |
| Membership + reminders | 0.1 | 0.1 |
| **total** | **36.6** | **22.2** |

Database work per instance:

| server | `linear-10` transactions / WAL | `one-task` transactions / WAL |
|---|---:|---:|
| Citus workers (journal) | 14.9 / 6.7 KB | 6.0 / 2.0 KB |
| Read-model primary | 4.0 / 5.5 KB | 4.0 / 2.7 KB |
| Membership + reminders | 0.1 / 0 | 0.0 / 0 |

## Findings

1. **The stream hop was the latency floor** (fixed by #918). On `main` every script task and gateway
   condition went through a stream (Kafka) to a handler grain: ~100 ms per step, so a 10-step process
   could not get under ~1.3 s on an idle cluster. With conditions in the instance grain and scripts
   dispatched directly to a `[StatelessWorker]`, a step costs a few ms; p99 dropped 1.57 s → 73 ms at
   100/s. The CPU per instance stayed about the same — the Kafka hop was cheap in CPU, expensive in time.
2. **A membership-table read per activation** (fixed by #919). `CorePlacementDirector` (and the
   projector's director from #917) called `IManagementGrain.GetDetailedHosts` for every activation —
   a single grain for the cluster that reads the membership table from Postgres. `pg_stat_statements`
   on the ops database: ~1.3 `ReadAll` per started instance, from every silo; 4.3 transactions per
   instance on a database that should see none. With a 5 s host cache: 0.1, Core CPU −25 %, p99 at
   100/s 255 → 73 ms, and 150/s became sustainable. Exactly the kind of shared point the roadmap removes.
3. **A warning per activity** (fixed by #919, #868 part 1). `WorkflowEventsPublisher[5001]` logged
   every `WorkflowActivityExecutedEvent`: 338 k console lines in one minute at 200/s.
4. **`DISCARD ALL` on every pooled-connection reuse.** Npgsql resets session state when a pooled
   connection is reused: ~7 `DISCARD ALL` per instance on the read model, each counted as a server
   transaction (18 → 4 transactions per instance together with #908). The stack sets
   `No Reset On Close=true`; Fleans keeps no session state on pooled connections. Worth a line in the
   production configuration docs.
5. **Overload stops the cluster instead of degrading it** — see [§ Overload](#overload-and-load-shedding).
   Orleans load shedding does not help; a REST rate limit at ~75 % of the measured ceiling does.
6. **Citus on arm64.** `citusdata/citus` is published for amd64 only; emulated, the coordinator was
   several times slower and was OOM-killed at 50 starts/s with 768 MB. The stack builds Citus 12.1 from
   source on `postgres:16` (`tests/load/citus/Dockerfile`).
7. **PgBouncer is not in the stack.** In transaction mode it breaks the session-level advisory lock
   that serializes the schema migrations at startup (`EnsureDatabaseSchemaAsync`: lock and unlock may
   run on different server connections). Production setups with PgBouncer need the migrations to run
   over a direct connection (e.g. a `ConnectionStrings:fleans-migrations` override, or a
   transaction-scoped lock) — a follow-up, not needed at this size.

## Overload and load shedding

`one-task` (`target + #919`), fresh stack per row; 1-minute windows at 200 → 250 → 300 offered/s:

| run | 200/s | 250/s | 300/s | 100/s afterwards |
|---|---|---|---|---|
| no limits | 200.3 /s, p99 0.31 s | **128 /s**, 845 left running | **0 completed** (3 467 started) | — (projector unhealthy) |
| Orleans load shedding at 80 % CPU (#913) | 200.0 /s, p99 0.23 s | 129 /s, 241 left running | **0 completed** (194 started) | — |
| REST limit 2 × 100/s (≈ 190 accepted/s) | 188 /s, p99 0.23 s | 180 /s (24 % → 429) | **0 completed** (37 % → 429) | **0 completed** |
| REST limit 2 × 75/s (150/s) | | | **142.5 /s, p99 0.23 s** (52 % → 429) | 100 /s, p99 0.21 s |

- Past the ceiling the cluster does not degrade, it stops: placement and grain calls time out after
  30 s (`PlacementService`, `CallbackData[100157]`), the stream consumers — projector and the plugin
  host, the two lowest CPU weights — fall behind, redeliveries pile up (`StreamProvider[361474169]`,
  Polly retries) and membership probes between silos fail. It does not recover without a restart
  (`overload/log-summary.txt`).
- **Orleans load shedding does not prevent it**: it rejects only new client requests at a silo's CPU
  threshold, while the backlog that kills the cluster is already inside (stream redeliveries,
  activations).
- **Admission control below the ceiling does**: the ASP.NET Core rate limiter on the `Api` role
  (`RateLimiting:WorkflowMutation`, existing) at ~75 % of the measured ceiling kept completions flat
  with 429s for the rest. At ~95 % of the ceiling it still collapsed: the sustainable rate under
  contention is lower than the peak measured on an idle start.
- For the roadmap's "backpressure at the edge" this means: a limit per `Api` instance sized from the
  measured ceiling (or, better, an adaptive one fed by `fleans.journal.append.duration` / projector lag,
  #916), and stream consumers that shed or slow producers instead of redelivering into a saturated
  cluster. The current limiter is a fixed window per client IP — behind a proxy that is one partition
  per `Api` instance, which happens to be what this needs.

## Resource estimate for Zeebe's numbers

Zeebe's published points are collected in [`zeebe-reference.md`](zeebe-reference.md). The estimate
scales the measured per-instance cost linearly — which is what the target architecture is for: the
hot path is the instance grain (scales with Core silos) and the journal (scales with Citus shards);
everything that was one grain or one database for the cluster is gone or sharded.

```
vCPU(T) = (idle + T × cost) × k / util      + HA
```

- `cost`, `idle` — the measured fit above (laptop CPU).
- `k = 1.5` cloud vCPU per laptop core. One M1 Pro performance core does roughly the work of
  1.5–2 vCPU of a current x86 cloud instance (a vCPU is a hyperthread); 1.5 is the conservative end.
  Docker Desktop's VM overhead (~45 % `sys` at saturation) is inside the measured cost, so the
  estimate errs high.
- `util = 0.65` — size for 65 % CPU (bursts, a lost node, p99 stays flat below ~80 %).
- HA — what the laptop does not run: a streaming standby per journal node (+30 % of journal CPU) and
  Kafka replication factor 3 (+150 % of Kafka CPU). The read-model standby is already measured.

| target | process | cloud vCPU (sized at 65 %) | with HA | Zeebe for the same point |
|---|---|---:|---:|---|
| 150 PI/s | `one-task` | 9 | **11** | Z6: 3 brokers + 2 gateways, RF 3 (weekly standard load) |
| 210 PI/s | `one-task` | 12 | **15** | Z3: 3 brokers × 1.35 CPU ≈ **4 CPU** at the ceiling (high back-pressure), gateways and workers extra |
| 500 PI/s, p99 < 1 s | `one-task` | 27 | **32** | Z1: 8 brokers × 8 vCPU = **64 vCPU** (several service tasks per instance) |
| 500 PI/s, p99 < 1 s | `linear-10` | 45 | **51** | Z1 (same) |
| 1 000 PI/s (roadmap level 1) | `linear-10` | 87 | **98** | — |
| 2 000 PI/s | `linear-10` | 171 | **192** | Camunda's 2025 tests are configured for 2 000 PI/s (no hardware published) |
| 10 000 PI/s (roadmap level 2) | `linear-10` | 846 | **946** | Z5 (2019, historical): 14 000 PI/s on 24 brokers × 7 CPU = 168 CPU, 1 task |

How the 500 PI/s `linear-10` cluster breaks down (with HA, 65 % sizing): ~19 vCPU of Core silos
(e.g. 3 × 8 vCPU), journal ~13 vCPU (2–3 Citus workers + standbys), read model ~3 vCPU + replica,
Kafka ~5 vCPU over 3 brokers, REST / projector / workers ~11 vCPU.

**Apples to apples.** Zeebe's figures count brokers only — no gateways, job workers, exporters,
Elasticsearch or Operate. Fleans' figures count everything that runs, including its read model
(Operate's role). On Zeebe's own process at the ceiling, the Fleans engine tiers (Core, journal,
projector, read model, Kafka, ops) need ≈ 4.5 laptop cores ≈ **7–8 vCPU with HA for 210 PI/s, vs
Zeebe's ~4 broker CPU** — Zeebe is about twice as efficient per instance on this process. At the
Z1 point (500 PI/s, p99 < 1 s), Fleans' total, everything included, lands at **0.5–0.8× Zeebe's
broker vCPU**; how close to 1.0 depends on how many of the steps are external tasks (only one per
instance was measured; each one is a Kafka record, a plugin activation and a callback). The latency
target held on the laptop up to ~85 % host CPU.

**What breaks first beyond the CPU estimate** (per-instance database work × rate):

| at 10 000 PI/s `linear-10` | load | verdict |
|---|---|---|
| Journal (Citus) | 150 k transactions/s, 67 MB/s WAL | spread over shards: ~16 workers × 9 k tx/s — fine; the coordinator must not be the router (Citus "query from any node") |
| Read-model primary | 40 k transactions/s, 55 MB/s WAL | **one primary at its limit** — needs the open #861 item (batched upserts across instances, ~10× fewer transactions) or the read model in Citus as well |
| Kafka | ~10 notifications per instance → 100 k records/s | a 3–6 broker cluster with 64–128 partitions |
| Membership / reminders | ~0 per instance after #919 | fine; timers are a separate axis (`timers-1m`) |

**Caveats.** One laptop, one-minute windows, a linear extrapolation over three rates; the
conversion factor `k` is an estimate, not a measurement. Not covered: waiting instances (memory per
idle instance, the `long-lived` scenario), timers at scale (`timers-1m` needs more than one host),
payload size (Zeebe's benchmarks use ~32 KB; these runs use none — `TP_PAYLOAD_KB` exists), and the
scaling efficiency itself (2 → 4 → 8 silos), which only a multi-node run can show.

## Reproduce

```bash
bash tests/load/build-images.sh                      # fleans-api / -worker / -plugin + fleans-citus
cd tests/load && docker compose up -d --wait && docker compose exec nginx nginx -s reload
K6_TARGET_URL=http://localhost:80 k6 run scripts/setup.js
RATES="50 100 150" PROCESS=load-linear-10 DURATION=1m WARMUP=20s bash scripts/throughput-ceiling.sh
RATES="100 150 200" PROCESS=load-one-task DURATION=1m WARMUP=20s bash scripts/throughput-ceiling.sh
python3 scripts/capacity.py results/throughput-<timestamp>
```

Overrides used for the comparison rows are in `overrides/` (`docker compose -f docker-compose.yml -f
results/target-arch-2026-10-24/overrides/<file>.yml …`, run from `tests/load`). Raw files: `main/`,
`final/` (`target`), `final-cache/` (`target + #919`), `collapse/` (fresh stack at 200/s),
`overload*/` — per rate `<process>-<rate>.{k6,sql,cpu,db}.txt`, plus a log summary per overload run.
