# 79 — Silo failover drill

**Feature:** #723 — a Core silo that crashes or is stopped while it hosts waiting instances does not
lose them. The instances restart on the surviving silos, their timers fire from the reminder store,
and every task and timer completes exactly once. The website page
`website/src/content/docs/reference/high-availability.md` publishes the numbers recorded below.

**Automated coverage (not repeated here):** `SiloFailoverTests` (Application) — a 3-silo test
cluster, an instance waiting on a task and a 5 s timer on a secondary silo, the silo killed or
stopped, the task completed through retries, the timer fired from a survivor, the workflow
completed with each activity exactly once and the read model caught up. What the test cluster cannot
show: real containers, Postgres membership and reminders, Kafka streams, the REST API in front, and
the time a real cluster takes to declare a silo dead. That is this drill.

Fixture: `silo-failover.bpmn` (process `silo-failover`: fork → user task `approve` (assignee
`drill`) and a 90 s timer `wait` → join → end). Script: `drill.py` (Python 3.9+, standard library
only).

## Prerequisites

- Docker with about 10 CPUs / 12 GB for the VM, the .NET SDK.
- The load-test stack, built from the branch under test:

  ```bash
  bash tests/load/build-images.sh
  cd tests/load && docker compose up -d --wait && cd ../..
  ```

  It runs 3 Core silos (`fleans-load-fleans-core-1..3`), membership and reminders on Postgres
  (`postgres-ops`), streams on Kafka, and the REST API behind nginx on `http://localhost`.

After each run, bring the stopped silo back and let it settle before the next one:
`(cd tests/load && docker compose up -d --wait fleans-core) && sleep 30`.

## A. Crash (`docker kill`)

1. `python3 -I tests/manual/79-silo-failover/drill.py --victim fleans-load-fleans-core-1`
2. Expect: `Claims and completions accepted: 150/150`, the last within ~10 s of the signal; the
   instances on the killed silo show up under `after retries`.
3. Expect: `Completed: 150/150, failed: 0, not finished: 0, task or timer not completed exactly
   once: 0` and a timer lag under 2 s.
4. When the silo was declared dead:
   `docker exec fleans-load-postgres-ops-1 psql -U postgres -d ops -At -c "select hostname, status, suspecttimes from orleansmembershiptable where status = 6"`
   — status 6 (`Dead`) with two votes in `suspecttimes`, a few seconds after the kill.

## B. Graceful stop (`docker stop -t 60`)

1. `python3 -I tests/manual/79-silo-failover/drill.py --victim fleans-load-fleans-core-2 --stop`
2. Expect: the container is gone within a few seconds (it leaves the cluster on SIGTERM), 150/150
   accepted within a few seconds, at most a handful of retries, 150/150 completed exactly once,
   timer lag under 2 s.

## C. Crash while the timers fall due

1. `python3 -I tests/manual/79-silo-failover/drill.py --victim fleans-load-fleans-core-3 --kill-after 88`
   (the timers are due 90 s after the start, 2 s after the kill).
2. Expect: 150/150 completed exactly once — no timer lost or fired twice.
3. Expected limitation: part of the timers fire one reminder period (60 s) late, and some calls
   wait for the 30 s response timeout before they are retried (`Transport errors or timeouts`).
   More than 70 s of timer lag, or a timer that never fires, is a failure.

Stop the stack: `cd tests/load && docker compose down -v`.

## Recorded results

2026-11-04, branch `feature/723-silo-failover` (on `main` a5737c49), MacBook, Docker VM 10 CPUs /
12 GB, 150 instances per run, Orleans membership options at their defaults.

| Run | Silo declared dead | Accepted (claim + complete) | Retried | Timer lag | Completed |
|---|---|---|---|---|---|
| A. `docker kill` core-2 | 5.7 s after the kill | all within 6.8 s | 37 | — (report crashed, fixed) | — |
| A. `docker kill` core-3 | 5.5 s | all within 6.5 s; first-try median 0.1 s | 30 | — (report crashed, fixed) | — |
| A. `docker kill` core-1 | 6.0 s | all within 7.0 s; first-try median 0.1 s | 29 | median 0.0 s, max 0.6 s | 150/150, exactly once |
| B. `docker stop -t 60` core-3 | left by itself; container gone after 1.6 s | all within 1.6 s | 5 | median 0.0 s, max 0.6 s | 150/150, exactly once |
| C. `docker kill` core-2, `--kill-after 88` | — | all within 30.4 s; 5 calls timed out (30 s) | 19 | median 60.0 s, max 60.3 s | 150/150, exactly once |
| C. `docker kill` core-3, `--kill-after 88` | — | all within 30.9 s; 7 calls timed out (30 s) | 13 | 83 of 150 late by ~60 s, max 60.6 s | 150/150, exactly once |

Run C shows the documented limitation: a timer whose callback cannot reach its instance while the
silo is being declared dead is retried one reminder period (1 minute) later.
