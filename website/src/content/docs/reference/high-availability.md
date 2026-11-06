---
title: High availability
description: What happens to running workflows when a Fleans silo crashes or restarts, how long failover takes (measured), which stores must be highly available themselves, and the known limitations.
sidebar:
  order: 6
---

A Fleans cluster keeps running workflows when an engine silo goes away. Every workflow instance is
an Orleans grain whose state lives in the database, not in the silo's memory, so a lost instance
starts again on a surviving silo and continues from its last saved step. This page describes what
that looks like in practice, with numbers from a measured drill, and what you need to run for it to
work.

## What survives the loss of a silo

| Kind of work | What happens when its silo dies |
|---|---|
| A waiting instance (user task, message, signal, timer, external task) | Restarts on a surviving silo at its next call or timer. Nothing is lost: every step was written to the journal before the call that made it returned. |
| A BPMN timer | Fires from a surviving silo. Timers are Orleans reminders stored in the reminder store (Postgres or Redis), not in the silo. |
| A call to the lost silo (complete a task, send a message) | Fails or times out until the cluster declares the silo dead. Retry it; see [Clients must retry](#clients-must-retry). |
| A stream message (read-model update, plugin task) | Delivered again by another silo. Kafka, Redis and Azure Queue streams are at-least-once; the engine ignores a message it has already handled. |
| A script or plugin task that was running on the lost silo | Runs again on another silo: a script is sent again after its result timeout, a plugin task when its stream message is delivered again. A plugin's side effect can therefore happen twice; make plugin calls idempotent. |

Each activity instance still completes or fails **exactly once**: a repeated completion, timer or
message for an activity that is no longer active is ignored.

## Failover time (measured)

Measured with the [drill below](#run-the-drill-yourself) on one laptop (Docker, 10 CPUs): the
[scalable layout](/fleans/guides/topology-scalable/) with 3 Core silos, membership and reminders on
Postgres, streams on Kafka, Orleans membership settings at their defaults. Each run started
150 instances that wait on a user task and a 90-second timer, then stopped one Core silo.

| Scenario | Silo declared dead | All 150 tasks completed (claim + complete) | Timer lag | Result |
|---|---|---|---|---|
| `docker kill` (crash) | 6 s after the kill | within 7 s; the 29 instances on the lost silo needed retries | max 0.6 s | 150/150 completed, task and timer each exactly once |
| `docker stop` (graceful) | the silo left by itself after 1.6 s | within 1.6 s; 5 calls retried | max 0.6 s | 150/150, exactly once |
| `docker kill` 2 s before the timers were due | 6 s after the kill | within 31 s; 13 calls waited out the 30 s response timeout | 83 of 150 timers fired **60 s late** | 150/150, exactly once |

Instances on the surviving silos kept answering in about 0.1 s throughout.

What drives the numbers:

- **A crash is noticed by the other silos' probes.** Silos probe each other; when enough of them
  fail to reach a silo, they vote it dead in the membership table and its grains move. In the drill
  that took about 6 seconds, because a killed container stops answering immediately. A silo on a
  host that drops off the network without closing its connections takes longer: the probes have to
  time out.
- **A graceful stop is not noticed at all, the silo says goodbye.** On SIGTERM a silo leaves the
  membership table, hands over its grains and its share of the timers, then exits. Give it time to
  do that: the Helm chart sets `terminationGracePeriodSeconds: 60` and a shutdown timeout 10 s
  shorter. With plain Docker, `docker stop` waits only 10 seconds before SIGKILL; use
  `docker stop -t 60` or `stop_grace_period: 60s` in Compose.
- **On Kubernetes, a deleted pod is removed from the cluster at once.** With
  `Fleans:Kubernetes:Enabled=true` (the chart's default) the silos watch the pods, so a pod that is
  deleted or evicted is declared dead without waiting for probes.

## Known limitations

- **A timer that falls due during failover fires up to one minute late.** When a timer is due, its
  callback calls the workflow instance. If that instance was on the lost silo and the silo is not
  yet declared dead, the call fails and the timer tries again one minute later (the timer
  reminder's retry period). The timer is never lost and never fires twice, but it is late. In the
  drill this affected 83 of 150 timers that were due 2 seconds after the kill. Timers due after the
  cluster has recovered fire on time.
- **A call that was already on its way to the lost silo can wait for the response timeout**
  (30 seconds, Orleans' default) before it fails. A REST call to the API then returns an error
  after about 30 seconds; retry it.
- **The single-PostgreSQL layout is not highly available.** The
  [simplest layout](/fleans/guides/topology-single-postgres/) runs one engine silo with in-memory
  streams: if that silo goes down, the engine is down until it restarts, and stream messages
  in flight at that moment (read-model updates, plugin tasks) are lost. Instances, timers and
  their journal are still safe in PostgreSQL and resume after the restart. Use the
  [scalable layout](/fleans/guides/topology-scalable/) with at least two Core silos when you need
  failover.

## Clients must retry

During the failover window, calls to instances on the lost silo fail with a 5xx status or time out.
A client that completes tasks or sends messages should retry with back-off for at least a minute:

- **Complete an activity with its `ActivityInstanceId`** (`POST /Execution/complete-activity`, or an
  external worker through the SDK). A repeated completion of the same activity instance is
  ignored, so the retry is safe even when the first call did succeed.
- **Send messages with an `IdempotencyKey`** (`POST /Execution/message`). A retried message with the
  same key is delivered once.
- **User tasks:** a repeated claim by the same user succeeds again. A retried complete returns
  `404` or `409` when the first attempt did land; after a failed attempt, read
  `GET /Instances/{id}/state` to tell that apart from a real conflict.
- **Signals are not deduplicated**: a retried `POST /Execution/signal` broadcasts again.

## What must be highly available

Silos are replaceable; the stores they share are not. Run each of these highly available in
production, or the whole cluster stops when it does:

| Store | Used for | Without it |
|---|---|---|
| Membership store (Postgres or Redis) | Which silos are alive | No silo can be declared dead and no silo can join. Running silos keep serving. |
| Reminder store (Postgres or Redis) | BPMN timers | Timers stop firing until it is back; they are not lost. |
| Journal database (Postgres / Citus) | Workflow state | Every workflow step fails. |
| Read-model database | Lists, instance state of finished instances | Lists and finished-instance reads fail; workflows keep running and the read model catches up afterwards. |
| Stream provider (Kafka, Redis, Azure Queue) | Plugin tasks, read-model updates, signals | Plugin tasks and read-model updates wait. |

Recommended: PostgreSQL with Patroni or a managed service (one for membership and reminders, one
for the read model, Citus with a Patroni pair per node for the journal), Kafka with a replication
factor of 3. Redis replication is asynchronous, so a Redis failover can drop an acknowledged write;
for membership and timers in production, prefer Postgres. Details per layout are in
[Recommended for scale](/fleans/guides/topology-scalable/).

Run **at least two Core silos** (three is better: a silo is declared dead by votes of the others),
and spread them over nodes or availability zones.

## Health endpoints

Every host serves two anonymous endpoints:

| Endpoint | Use as | Fails when |
|---|---|---|
| `/alive` | liveness probe | the process does not answer |
| `/health` | readiness probe | the process does not answer, a database it needs is unreachable, or Redis is unreachable on a host that uses it |

The Helm chart wires both. Keep `/alive` as the liveness probe: if liveness used `/health`, a
database outage would make Kubernetes restart every pod in a loop. `/health` does not check
cluster membership; the silos' own probes handle that. See [Observability](/fleans/reference/observability/#health-checks).

## Rolling upgrades

A rolling upgrade is a series of graceful stops: each old silo leaves the cluster, its instances
restart on the other silos, and a new silo joins. Keep these the same across old and new pods:

- `Orleans__ClusterId` and `Orleans__ServiceId`, and the membership store: otherwise the new silos
  form a second cluster next to the old one.
- The reminder and stream providers.

Use `maxUnavailable: 1` (or `0` with `maxSurge: 1`) so that Core silos are replaced one at a time,
and give each silo its full shutdown grace period. Release notes say when a version cannot run next
to the previous one (for example a stream format change) and needs a full stop instead.
[Deployment → Update strategy](/fleans/reference/deployment/#update-strategy) has the commands for
Kubernetes, Compose and systemd.

## Run the drill yourself

The drill is a script in the Fleans repository:
[`tests/manual/79-silo-failover`](https://github.com/nightBaker/fleans/tree/main/tests/manual/79-silo-failover).
It deploys a process, starts instances that wait on a user task and a timer, stops a silo
(`docker kill` or `docker stop`), completes the tasks while the cluster recovers, and prints the
failover window, the timer lag and whether every instance completed exactly once. Point it at your
own cluster with `--url` and `--victim <container>` to measure your settings.
