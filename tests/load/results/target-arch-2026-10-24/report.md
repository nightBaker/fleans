# Target architecture on a laptop — 2026-10-24/25

The [Road to 1.0.0](https://nightbaker.github.io/fleans/roadmap/v1/#target-architecture) target
architecture, scaled down to one laptop (`tests/load/docker-compose.yml`), measured with the
roadmap's headline metric — completed instances per second and p99 process duration — plus the
scaling matrix at laptop scale, overload behaviour, timers and waiting instances, and a resource
estimate for Zeebe's published numbers.

## Summary

| | `main` | all roadmap PRs (`target-all`) |
|---|---|---|
| `linear-10` (10 script tasks) at 100/s | p99 **1.57 s** | p99 **0.13 s** |
| `linear-10` ceiling at p99 < 1 s | 100/s (never under 1 s) | **150–200/s** |
| `one-task` (Zeebe's benchmark process) ceiling | 150/s measured | **340/s**, p99 0.84 s |
| CPU per instance, all tiers, laptop | ~40 ms / ~26 ms | **28.9 ms / 16.0 ms** |
| past the ceiling | stops, does not recover | degrades: 429s, keeps completing ~340/s |
| scaling efficiency, silos 1 → 2 → 3 | — | **0.87, 0.88** |
| scaling efficiency, journal nodes 1 → 2 | — | **0.87** |

- **Zeebe comparison** ([§ Resource estimate](#resource-estimate-for-zeebes-numbers)):
  - Zeebe's 2023 target was **500 PI/s at p99 < 1 s on 8 brokers × 8 vCPU = 64 vCPU** (brokers
    only). Fleans needs an estimated **~45 vCPU** for 500 `linear-10`/s and **~26 vCPU** for 500
    `one-task`/s — with high availability, sized for 65 % CPU, everything included (REST, read model).
  - On Zeebe's own process at its ceiling (210 PI/s on 3 × 1.35 CPU brokers ≈ 4 CPU), Fleans'
    engine tiers need ≈ 7 vCPU at the same load: **Zeebe is ~1.8× more CPU-efficient per instance**,
    but its figure excludes Operate / Elasticsearch, which Fleans' read model replaces.
  - Roadmap level 1 (1 000 `linear-10`/s): ~**90 vCPU**; level 2 (10 000/s): ~**830 vCPU**.
- **Hot-path costs found and fixed by these runs**: a membership-table read per activation (#919),
  a warning per activity (#919), custom-task handler grains that never deactivated and filled the
  plugin host (#928), waiting instances kept in memory for 15 minutes (#929), `DISCARD ALL` per pooled
  connection reuse (stack config), the PgBouncer-incompatible migration lock (#924).

## Setup

**Host:** MacBook Pro, Apple M1 Pro (8 performance + 2 efficiency cores), 16 GB; Docker Desktop
29.2 with 10 CPU / 12 GB. k6 v0.54 on the same host. No other workload during the runs.

**Stack** (`tests/load/docker-compose.yml`):

| Tier | Services |
|---|---|
| REST | nginx → 2 × `fleans-api` (`Fleans:Role=Api`, an Orleans client, #913) |
| Orleans | 3 × `fleans-core`, 1 × `fleans-projector` (#917), 1 × `fleans-worker`, 1 × `fleans-plugin` (`e2e-probe` external task) |
| Journal | Citus 12.1 (built from source for arm64): coordinator + 2 workers, 32 shards by `InstanceId` |
| Read model | Postgres 16 primary + streaming hot standby; `ConnectionStrings:fleans-query` → standby |
| Ops | Postgres 16: Orleans membership + reminders |
| Streams | Kafka 7.9 (KRaft, 1 broker), implicit subscriptions only; no Redis anywhere |

CPU is shared by `cpu_shares` weights (no caps, except in the scaling matrix). Npgsql pools are
capped (30 / 10) with `No Reset On Close=true`.

**Builds:**

| Name | Code |
|---|---|
| `main` | `origin/main` at `db9e1696` (REST on the Core silos, Redis for the health check, no projector role — `overrides/main-override.yml`, built with `TAG=main`) |
| `target` | `main` + #908 (projection round trips) + #913 (Api role, optional Redis, load shedding) + #914 (cached start listeners) + #915 (signal buckets) + #916 (scale metrics) + #917 (projector role, replica reads) + #918 (in-process conditions, direct scripts) |
| `target + #919` | `target` + #919 (placement host cache, no warning per activity) |
| `target-all` | `target + #919` + #924 (PgBouncer-safe migration lock) + #926 (batched read-model upserts) + #927 (message-start deduplication) + #928 (adaptive admission control, custom-task handlers deactivate) + #929 (idle instances collected after 2 min) |

**Method:** `scripts/throughput-ceiling.sh` — per rate a 20 s warm-up and a **1-minute** measured
window at a constant start rate (open model), after a 60 s unmeasured pre-warm; results from the
read model (`CompletedAt − CreatedAt`, both journal timestamps); CPU = `docker stats` average over
the window. The roadmap asks for 5 + 30 minutes — a laptop run is a shape, not a published number.
A rate counts as sustained when completions match it (≥ 97 %), nothing is left running and
p99 < 1 s.

## Results

### `linear-10` — 10 script tasks in a row

| offered /s | `main` p50 / p99 | CPU | `target + #919` p50 / p99 | CPU | `target-all` p50 / p99 | CPU |
|---:|---|---:|---|---:|---|---:|
| 50 | 1.39 s / 1.53 s | 3.0 | 23 ms / 68 ms | 2.7 | | |
| 100 | 1.47 s / 1.57 s | 5.0 | 25 ms / 73 ms | 5.0 | **25 ms / 130 ms** | 4.7 |
| 150 | — | — | 38 ms / 404 ms | 6.5 | **31 ms / 194 ms** | 6.1 |
| 200 | — | — | — | — | 135 ms / 1.01 s (edge) | 7.3 |

CPU in cores (sum over the stack's containers). `main` sustains 100/s too, but never under 1 s: its
p50 is the stream hop (~100 ms per script step × 10 + the start). `target` without #919 collapsed at
150/s (p99 39 s).

### `one-task` — Zeebe's benchmark process (start → one external task, 50 ms → end)

| offered /s | `main` p50 / p99 | CPU | `target + #919` p50 / p99 | CPU | `target-all` p50 / p99 | CPU |
|---:|---|---:|---|---:|---|---:|
| 100 | 139 / 217 ms | 3.2 | 139 / 216 ms | 2.8 | | |
| 150 | 146 / 227 ms | 4.7 | 140 / 224 ms | 4.2 | | |
| 200 | | | 145 / 264 ms | 5.1 | 141 / 233 ms | 4.5 |
| 250 | | | collapses (§ Overload) | | 145 / 239 ms | 5.3 |
| 300 | | | | | 164 / 410 ms | 6.3 |
| 350 | | | | | **215 / 839 ms** (340 completed/s) | 7.2 |
| 400 | | | | | 388 ms / 1.23 s — 342/s completed, 12 % answered 429 | 7.9 |

50 ms of the ~140 ms p50 is the task itself; the rest is the start, the custom-task event's Kafka hop
to the Plugin silo (stream poll) and the completion callback — the floor for an external task today.

### Where the CPU goes (`target-all`)

From `scripts/capacity.py` (CPU = idle + rate × cost, fitted over the sustained rates):

| tier | `linear-10` ms / instance | `one-task` ms / instance |
|---|---:|---:|
| Core silos (workflow grains) | 11.2 | 4.4 |
| Journal (Citus coordinator + workers) | 8.1 | 3.9 |
| Kafka | 3.0 | 1.8 |
| Projector silo | 2.3 | 2.1 |
| Read model (primary + standby) | 1.9 | 1.2 |
| REST (Api role + nginx) | 1.4 | 1.1 |
| Worker silo (scripts) | 0.5 | 0.4 |
| Plugin (external task) | 0.4 | 0.9 |
| Membership + reminders | 0.0 | 0.1 |
| **total** | **28.9** | **16.0** |

Database work per instance:

| server | `linear-10` transactions / WAL | `one-task` transactions / WAL |
|---|---:|---:|
| Citus workers (journal) | 15.0 / 6.2 KB | 6.0 / 1.6 KB |
| Read-model primary | 2.4 / 4.8 KB | 1.6 / 2.3 KB |
| Membership + reminders | 0.0 | 0.0 |

The read-model batches (#926) are small at laptop rates — a batch fills only when many instances
project at once; the unit measurement (64 instances × 3 projections) is 0.05 transactions per
instance, so the read model's cost falls further as the rate grows.

## Scaling matrix (laptop scale)

`scripts/scaling-matrix.sh` on `target-all`: the maximum sustained `linear-10` rate as one tier is
made the bottleneck and then grown. Scaling efficiency = throughput gain ÷ resource gain; the
roadmap's target is ≥ 0.8.

| axis | resources | sustained | efficiency |
|---|---|---:|---:|
| Core silos, 0.35 CPU each (`overrides/core-capped.yml`) | 1 silo | 46 /s | — |
| | 2 silos | 80 /s | **0.87** |
| | 3 silos | 121 /s | **0.88** |
| Citus workers, 0.5 CPU each (`overrides/journal-capped.yml`) | 1 worker (32 shards) | 79 /s | — |
| | 2 workers | 138 /s | **0.87** |

- The silo axis counts a rate as sustained on throughput with p99 < 3 s: a 0.35-CPU cap is enforced
  by CFS throttling, which adds up to ~65 ms per cross-silo hop, so p99 grows with the number of
  silos for a reason a production silo does not have (with p99 < 1 s: 40 → 80 → 52/s, the third
  silo's p99 crossing 1 s at 60/s while it still completed everything). The journal axis uses the
  normal p99 < 1 s.
- Only 1 → 3 silos and 1 → 2 journal nodes fit on one host; the roadmap's 2 → 16 / 1 → 8 matrix
  needs a multi-node run. What the laptop shows is that neither axis has a shared point left that
  bends the curve at this size.

## Overload and admission control

`one-task`, fresh stack per row; 1-minute windows at the offered rates:

| build / setting | 200/s | 250/s | 300/s | 400/s | 500/s | 100/s afterwards |
|---|---|---|---|---|---|---|
| `target + #919`, no limits | 200 /s | **128 /s** | **0 completed** | | | — (projector unhealthy) |
| `+ #919`, Orleans load shedding 80 % | 200 /s | 129 /s | **0 completed** | | | — |
| `+ #919`, REST fixed limit ≈ 190/s | 188 /s | 180 /s | **0 completed** | | | **0 completed** |
| `+ #919`, REST fixed limit 150/s | | | 142.5 /s, p99 0.23 s | | | 100 /s |
| `+ #928` (handler fix), admission off ¹ | 200 | 250 | 300, p99 0.82 s | 397, p99 17.7 s | **34 /s** | 83 /s, still draining |
| `+ #928`, adaptive admission on ¹ | 200 | 250 | 300 | 398, p99 0.74 s | **272 /s, p99 1.2 s** (37 % → 429) | 100 /s, p99 0.23 s |
| `target-all` (this report's ceiling run) | 200 | 250 | 299 | 342, p99 1.23 s (12 % → 429) | | |

¹ From the #928 run on the same stack and host.

- **The collapse was memory, not CPU** (found by #928): every custom-task handler grain — one per
  instance — stayed activated for Orleans' 15-minute idle timeout. At 200 tasks/s the 384 MB plugin
  host filled within ~4 minutes and sat in garbage collection; the 30 s timeouts, stream
  redeliveries, Polly retries and failed membership probes in `overload/log-summary.txt` followed
  from that one stuck silo. #928 deactivates a handler once its task is done.
- **Orleans load shedding does not prevent a collapse** — it rejects only new client requests at a
  silo's CPU threshold. **Admission control at the REST edge does**: #928's adaptive concurrency limit
  (AIMD on the start latency, on by default, `Fleans:Admission:*`) answers 429 above the ceiling and
  keeps the cluster completing near its capacity; without it, 500/s offered dropped completions to
  34/s and the backlog took minutes to drain.

## Timers and waiting instances

`scripts/timers-1m.js`, scaled to what one host can arm and fire (`target-all`):

| run | armed | fired | timer lag p50 / p99 / max |
|---|---:|---:|---|
| 10 000 timers due within one minute (≈ 170 fires/s) | 9 960 | **9 960** | **0.03 / 0.09 / 0.40 s** — pass (p99 < 2 s) |
| 60 000 within one minute (≈ 1 000 fires/s), before #929 | 59 768 | 920 after 5 min | ops Postgres OOM-killed (384 MB), Core silos at their 1 GB limit |
| 60 000 within one minute, #929 + ops Postgres 768 MB | 57 464 | 2 094 after 1 min | Core silos at 1 GB once the timers fire |

- **A waiting instance costs ~27 KB of silo memory while activated** (Core silos 145 → 690 MB with
  20 000 waiting instances each). Orleans keeps an idle activation for 15 minutes, so before #929 every
  instance armed in the last 15 minutes was in memory; #929 collects an idle `WorkflowInstance` after
  2 minutes (`Fleans:Instances:IdleCollectionAge`). The silo's working set stayed at ~690 MB
  afterwards — .NET's server GC does not hand freed heap back to the OS — so the run cannot show the
  drop, only that the growth stopped.
- **60 000 timers due in one minute is ~1 000 fires/s — 3× the laptop's ceiling.** Every fire
  reactivates an instance (journal snapshot read), appends, projects and completes; at that rate the
  silos ran out of memory. 10 000 in a minute (170/s) passes with sub-second lag.
- The roadmap's `timers-1m` (1 million due within a minute = 16 700 fires/s) is a burst sizing
  question: at ~10 ms of laptop CPU per fire (one-task without the external task: Core + journal +
  projector) it needs ~170 laptop cores ≈ 390 vCPU for that minute, or the timers spread over a
  longer window. The reminder path itself (Postgres reminders, #886) delivered every timer on time
  wherever the silos had capacity.

## Resource estimate for Zeebe's numbers

Zeebe's published points are collected in [`zeebe-reference.md`](zeebe-reference.md). The estimate
scales the measured per-instance cost linearly — which the scaling matrix above supports at laptop
scale (efficiency 0.87–0.88 on both axes): the hot path is the instance grain (scales with Core
silos) and the journal (scales with Citus shards), and what was one grain or one database for the
cluster is gone, sharded or batched.

```
vCPU(T) = (idle + T × cost) × k / util      + HA
```

- `cost`, `idle` — the measured fit above (`target-all`, laptop CPU).
- `k = 1.5` cloud vCPU per laptop core. One M1 Pro performance core does roughly the work of
  1.5–2 vCPU of a current x86 cloud instance (a vCPU is a hyperthread); 1.5 is the conservative end.
  Docker Desktop's VM overhead (~45 % `sys` at saturation) is inside the measured cost, so the
  estimate errs high.
- `util = 0.65` — size for 65 % CPU (bursts, a lost node; p99 stays flat below ~80 %).
- HA — what the laptop does not run: a streaming standby per journal node (+30 % of journal CPU) and
  Kafka replication factor 3 (+150 % of Kafka CPU). The read-model standby is already measured.

| target | process | cloud vCPU (sized at 65 %) | with HA | Zeebe for the same point |
|---|---|---:|---:|---|
| 150 PI/s | `one-task` | 9 | **10** | Z6: 3 brokers + 2 gateways, RF 3 (the weekly standard load) |
| 210 PI/s | `one-task` | 11 | **13** | Z3: 3 brokers × 1.35 CPU ≈ **4 CPU** at the ceiling (high back-pressure), gateways and workers extra |
| 500 PI/s, p99 < 1 s | `one-task` | 22 | **26** | Z1: 8 brokers × 8 vCPU = **64 vCPU** (several service tasks per instance) |
| 500 PI/s, p99 < 1 s | `linear-10` | 37 | **45** | Z1 (same) |
| 1 000 PI/s (roadmap level 1) | `linear-10` | 71 | **87** | — |
| 2 000 PI/s | `linear-10` | 137 | **169** | Camunda's 2025 tests are configured for 2 000 PI/s (no hardware published) |
| 10 000 PI/s (roadmap level 2) | `linear-10` | 670 | **830** | Z5 (2019, historical): 14 000 PI/s on 24 brokers × 7 CPU = 168 CPU, 1 task |

How the 500 PI/s `linear-10` cluster breaks down (with HA, 65 % sizing): ~15 vCPU of Core silos
(e.g. 2–3 × 8 vCPU), journal ~12 vCPU (2 Citus workers + standbys), read model ~2 vCPU + replica,
Kafka ~8 vCPU over 3 brokers, REST / projector / workers ~8 vCPU.

**Apples to apples.** Zeebe's figures count brokers only — no gateways, job workers, exporters,
Elasticsearch or Operate. Fleans' figures count everything that runs, including its read model
(Operate's role). On Zeebe's own process at the ceiling, the Fleans engine tiers (Core, journal,
projector, read model, Kafka, ops: 13.6 ms per instance) need ≈ 4 laptop cores ≈ **7 vCPU with HA
for 210 PI/s, vs Zeebe's ~4 broker CPU** — Zeebe is ~1.8× more CPU-efficient per instance on this
process. At the Z1 point (500 PI/s, p99 < 1 s), Fleans' total, everything included, lands at
**0.4–0.7× Zeebe's broker vCPU**; how close to 1.0 depends on how many of the steps are external
tasks (only one per instance was measured; each is a Kafka record, a plugin activation and a callback).

**What breaks first beyond the CPU estimate** (per-instance database work × rate):

| at 10 000 PI/s `linear-10` | load | verdict |
|---|---|---|
| Journal (Citus) | 150 k transactions/s, 62 MB/s WAL | spread over shards: ~16 workers × 9 k tx/s — fine; the coordinator must not be the router (Citus "query from any node") |
| Read-model primary | ≤ 24 k transactions/s at laptop batch sizes, far fewer as batches fill (#926) | fine on one primary with replicas |
| Kafka | ~10 notifications per instance → 100 k records/s | a 3–6 broker cluster with 64–128 partitions |
| Memory | a completed instance deactivates at once (#860); a waiting one costs ~27 KB while activated, and with #929 only for 2 minutes after it was last touched | e.g. 1 000 timer or message wake-ups/s → ~120 k activations ≈ 3 GB across the Core silos — fine |
| Membership / reminders | ~0 per instance after #919 | fine; timers are their own axis (above) |

**Caveats.** One laptop, one-minute windows, a linear extrapolation over the sustained rates; the
conversion factor `k` is an estimate, not a measurement; the scaling matrix covers 1 → 3 silos and
1 → 2 journal nodes only. Not covered: payload size (Zeebe's benchmarks use ~32 KB; these runs use
none — `TP_PAYLOAD_KB` exists), the `long-lived` scenario (10 M waiting instances, then `linear-10`)
and `signal-fanout`.

## Findings

1. **The stream hop was the latency floor** (fixed by #918). On `main` every script task and gateway
   condition went through a stream (Kafka) to a handler grain: ~100 ms per step. With conditions in
   the instance grain and scripts dispatched directly to a `[StatelessWorker]`, a step costs a few ms;
   p99 at 100/s dropped from 1.57 s to 73–130 ms.
2. **A membership-table read per activation** (fixed by #919). `CorePlacementDirector` (and the
   projector's director from #917) called `IManagementGrain.GetDetailedHosts` for every activation —
   one grain for the cluster that reads the membership table from Postgres: ~1.3 `ReadAll` per
   started instance. With a 5 s host cache: Core CPU −25 %, p99 at 100/s 255 → 73 ms.
3. **A warning per activity** (fixed by #919, #868 part 1): 338 k console lines in one minute at 200/s.
4. **Custom-task handlers never deactivated** (fixed by #928): one activation per instance for
   15 minutes filled the plugin host and stopped the cluster past ~250 tasks/s.
5. **Waiting instances stayed in memory for 15 minutes** (fixed by #929): ~27 KB each.
6. **`DISCARD ALL` on every pooled-connection reuse**: ~7 per instance on the read model, each a
   server transaction. The stack sets `No Reset On Close=true`; documented by #924.
7. **PgBouncer in transaction mode broke the migration lock** (fixed by #924): a session-level
   advisory lock leaked into the pool; now transaction-scoped on its own connection.
8. **Citus on arm64.** `citusdata/citus` is amd64-only; emulated, the coordinator was several times
   slower and was OOM-killed. The stack builds Citus 12.1 from source (`tests/load/citus/Dockerfile`).

## Reproduce

```bash
bash tests/load/build-images.sh                      # fleans-api / -worker / -plugin + fleans-citus
cd tests/load && docker compose up -d --wait && docker compose exec nginx nginx -s reload
K6_TARGET_URL=http://localhost:80 k6 run scripts/setup.js
RATES="100 150 200" PROCESS=load-linear-10 STOP_ON_FAIL=1 bash scripts/throughput-ceiling.sh
RATES="200 250 300 350 400" PROCESS=load-one-task STOP_ON_FAIL=1 bash scripts/throughput-ceiling.sh
python3 scripts/capacity.py results/throughput-<timestamp>
OUT=results/scaling-$(date +%F) bash scripts/scaling-matrix.sh   # ~2 h; AXES=silos|journal for one axis
K6_TARGET_URL=http://localhost:80 K6_TIMERS_COUNT=10000 K6_TIMERS_RATE=200 K6_TIMERS_SLACK_S=60 k6 run scripts/timers-1m.js
```

Overrides used for the comparison rows are in `overrides/` (run from `tests/load`:
`docker compose -f docker-compose.yml -f results/target-arch-2026-10-24/overrides/<file>.yml …`).
Per run: `main/`, `final/` (`target`), `final-cache/` (`target + #919`), `final-all/` (`target-all`),
`collapse/`, `overload*/`, `timers*/`; the scaling matrix in `../scaling-2026-10-24/`. Each throughput
directory keeps `<process>-summary.txt` (the read-model row per rate) and `capacity.md` (the
`capacity.py` output: CPU per tier, transactions and WAL per instance); the per-rate
`<process>-<rate>.{k6,sql,cpu,db}.txt` files are not committed — rerun the commands above to get them.
