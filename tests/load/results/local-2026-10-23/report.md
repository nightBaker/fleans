# Local load test — 2026-10-23 — Road to 1.0 PRs, before vs after

Compares `main` before and after the first Road-to-1.0 PRs (#888–#898) on the same stack and host.

| | Commit | Orleans | Reminders |
|---|---|---|---|
| **before** | `89e6d6c3` | 10.0.1 | Redis |
| **after** | `db4381be` | 10.4.0 | Postgres (`postgres-reminders`), plus one run with `FLEANS_REMINDERS_PROVIDER=Redis` |

## Setup

- **Host:** MacBook, 10 cores; Docker Desktop with 10 CPUs / 8 GB; k6 on the same host.
- **Stack:** `tests/load/docker-compose.yml` of each commit — 3 Core + 2 Worker + 1 Plugin silo, Kafka streams,
  Redis clustering, Postgres. Images built with `build-images.sh` from each commit.
- **k6 scripts:** from `db4381be` for both runs (the REST API did not change).
- **nginx:** the tuned `nginx.conf` from this PR for both runs. With the old `events {}` config (1 worker × 512
  connections) every run above ~250 VUs failed with `EOF` — e.g. 96 % of 888 k `linear-saturation` requests —
  so those numbers measured nginx, not Fleans. The 2026-04-28 local report's 97 % error rate is likely the same effect.
- Each scenario ran on a fresh stack (`docker compose down -v && up -d --wait`, `setup.js`).

Absolute numbers are bounded by the laptop: during `linear-saturation` the host CPU is saturated (≈ 870 % of
1 000 %: Postgres ≈ 190 %, the three Core silos ≈ 450 %). Compare the columns, not the numbers with other hosts.

## `long-diverse` — step profile 10 → 25 → 50 → 100 VUs, 10.5 min

| run | iterations | completed | poll stalls | to `waitApproval` p50 | end-to-end p50 | end-to-end p95 | start p95 |
|---|---:|---:|---:|---:|---:|---:|---:|
| before | 5 008 | **100 %** | 0 % | 3 059 ms | 5 483 ms | 6 660 ms | 65 ms |
| after | 2 851 | **87.8 %** | 3.3 % | 2 068 ms | 4 538 ms | 7 753 ms | 250 ms |
| after, Redis reminders | 2 024 | **76.9 %** | 6.8 % | 2 066 ms | 4 505 ms | 5 836 ms | 73 ms |

The happy path got about a second faster, but stalled iterations wait minutes on 1 s timers
(`coolDown`, the child timer behind `branchCCall`) — see #902.

## `linear-saturation` — 50 → 2 000 VUs, 14 min

Successful starts per second, by stage (VUs):

| run | ok starts | failed | 25 | 50 | 75 | 100 | 150 | 200 | 350 | 500 | 750 | 1 000 | 1 500 | 2 000 |
|---|---:|---:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| before | 57 332 | 14.3 % | 96 | 231 | 298 | 97 | 2 | 4 | 6 | 15 | 42 | 76 | 52 | 71 |
| after | 75 438 | 6.1 % | 81 | 93 | 139 | 74 | 108 | 106 | 114 | 99 | 71 | 59 | 96 | 147 |

`after` no longer collapses past saturation (+32 % successful starts over the run, fewer errors). Its lower
numbers at 50–75 VUs did not reproduce in a follow-up with replicates (#903): single runs on this host swing
widely because Postgres randomly tips into WAL-flush contention, on both commits.

## `timers` — fresh stack, arming at up to 1 000 starts/s for 100 s

`timers-1m.js` with `K6_TIMERS_COUNT=100000 K6_TIMERS_RATE=1000`; the host could start only the counts below
within the arming window, so this is not the 1 M run from #886.

| run | armed | fired | not fired | lag p50 | lag p99 | lag max |
|---|---:|---:|---:|---:|---:|---:|
| before | 34 450 | 34 450 | 0 | 0.14 s | **0.94 s** | 1.6 s |
| after | 12 031 | 9 957 | 2 074 (9 min after due) | 17 s | **252 s** | 303 s |

## Findings

1. **Timers fire up to 5 minutes late (#902).** Orleans 10.4 `LocalReminderService.RegisterOrUpdateReminder`
   starts the local timer only when the receiving silo owns the grain's ring range; 10.0.1 always did. In this
   cluster registrations reach non-owners in steady state (3 min of `long-diverse` at 25 VUs: 161
   `RegisterReminder` and 81 `RemoveReminder` "not in my responsibility range" warnings, EventId 102910, on all
   six silos); such reminders wait for the owner's next `RefreshReminderListPeriod` (5 min). Both reminder
   providers are affected.
2. **Higher per-start cost since the async projection (#903).** The 2–3× gap at 50–75 VUs above is single-run
   noise: a follow-up with replicates measured 229 vs 232 starts/s at a fixed 50 VUs. What is systematic is
   #897's extra work per start: +50 % DB transactions, an awaited Kafka produce and a projector activation. It
   brings Postgres to WAL-flush contention sooner; timer-start latency is p50 21 → 132 ms at 400 starts/s.
3. **The load-test nginx dropped connections above ~250 VUs** — fixed by `nginx.conf` in this PR.

## Raw data

`before/`, `after/`, `after-redis-reminders/`: k6 JSON summaries (`long-diverse-summary.json`,
`linear-saturation-summary.json`) and the `timers-1m-lag.sql` output (`timers-lag.txt`).
