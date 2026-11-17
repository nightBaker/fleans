---
title: Common Issues
description: Symptoms, checks and actions for problems operators and workflow authors run into, plus an index of documented limitations.
---

Each entry gives the symptom, what to check, and what to do. For element-level coverage, see [BPMN Support](/fleans/concepts/bpmn-support/); for log EventIds, see [Logs and Debugging](/fleans/reference/troubleshooting-logs-debugging/). If you hit something not listed here, please [open an issue](https://github.com/nightBaker/fleans/issues).

## Instance shows *Failed*

**Affects:** any workflow where an activity fails and nothing handles the error.

**Symptom:** the instance's status is **Failed** (`isFailed: true` on `GET /Instances/{id}/state`) and one activity in `completedActivities` carries an `errorState`.

**Root cause:** the failing activity had no error boundary event and no enclosing error event sub-process, so the error ended the last live branch. A frequent trigger is a script task whose script DynamicExpresso cannot parse — e.g. `_context.items = new[] { 1, 2, 3 }` (implicitly-typed array literals are unsupported). A custom task that could not be handed to its worker queue (EventId 1169) fails the same way.

**Fix:** read the failing activity's `errorState` (also logged at `Error` level with EventId 1120). Fix the cause — for the array example use `new List<object> { 1, 2, 3 }` — or attach an error boundary / error event sub-process if the workflow should recover.

**Fixtures reproducing this:**

- [`tests/manual/71-unhandled-failure-fails-instance`](https://github.com/nightBaker/fleans/tree/main/tests/manual/71-unhandled-failure-fails-instance)

## PostgreSQL: `53300: sorry, too many clients already`

**Symptom:** requests fail or time out under load, throughput *drops* as load rises, and the logs show Npgsql errors with SQL state `53300`.

**Check:** count the worst case — every Fleans process opens one connection pool per connection string, and each pool grows to 100 connections unless `Maximum Pool Size` is set. Compare processes × pools × pool size (autoscaled workloads at `maxReplicas`) with the server's `max_connections`.

**Action:** cap every pool with `Maximum Pool Size=<n>` in the connection strings (Helm with the bundled PostgreSQL: `persistence.postgres.maxPoolSize.*` and `postgres.maxConnections`), or put PgBouncer in transaction mode in front. The formula and the per-process pool count are in [Persistence — Connection budget](/fleans/reference/persistence/#connection-budget).

## Instances stall after a stream queue-count change

**Symptom:** after changing `Fleans:Streaming:Redis:TotalQueueCount` or `Fleans:Streaming:Kafka:QueueCount` (or the Azure Queue `QueueNames` list), some instances stop advancing — custom tasks are never delivered, the read model stops updating — with nothing in the logs.

**Check:** that every host (all silos, and every host of a rolling restart) runs with the **same** value. Silos with different values route the same stream to different queues, and Orleans does not detect it.

**Action:** set the value once in shared configuration and restart every host. Instances that straddled the change may stall until they are reactivated; before 1.0 there is no drain procedure, so change queue counts on an idle or fresh cluster. See [Streaming — Orleans parallelism](/fleans/reference/streaming/#orleans-parallelism-all-queue-backed-providers).

## A stream event was skipped: a custom task never runs, or a list never updates

**Symptom:** a custom task stays *executing* and its worker never received it, or an instance's row in the lists stays behind its live state — and nothing was logged.

**Check:**

- How long the stream subscriber was failing: after `Fleans:Streaming:MaxEventDeliveryTime` (default 6 minutes; Kafka can override it with `DeliveryRetryWindow`) a silo **skips** the event for good, without a log line.
- On Redis streams, whether a backlog grew past `Fleans:Streaming:Redis:MaxStreamLength` (default 100 000 per queue) — Redis trims old entries whether or not they were delivered.
- On Kafka, the dead-letter topic and EventIds 11107 / 11113 / 12000 for records that could not be read.

**Action:** fix the cause of the failures, then size the delivery window above your slowest consumer outage and `MaxStreamLength` above your largest backlog ([Delivery window and Redis trimming](/fleans/reference/streaming/#delivery-window-and-redis-trimming)). A list that is behind catches up the next time the instance is activated, which asks the projector again (EventId 1133). A custom task whose event was skipped has to be failed or completed through the API.

## Warnings 11300 / 11301: no Core or Worker silo

**Symptom:** `CorePlacementDirector: no silo with 'core-' or 'combined-' prefix available …` (11300) or `WorkerPlacementFilterDirector: no silo with 'worker-' or 'combined-' prefix …` (11301) at Warning.

**Check:** which roles the cluster actually runs (`Fleans__Role` on each silo; the silo name starts with the role). 11301 is expected on a cluster with Core silos and no Worker silo — for example a default Helm install, which runs `Core` with `worker.enabled: false`. On Kubernetes, the pod name must start with the role.

**Action:** none is required — the grain runs on any silo instead, so scripts, conditional starts and the read-model projector then run on the Core silos. To keep that work off the Core silos, add Worker silos (Helm: `worker.enabled: true`). 11300 means no silo can host workflow instances in their preferred role: check that at least one `Core` or `Combined` silo is up. See [Configuration — Silo role](/fleans/reference/configuration/#silo-role).

## Pending timers lost after a Redis restart (default Helm install)

**Symptom:** after the chart's Redis pod restarts, timers that were waiting never fire, and stream entries that were not yet delivered are gone.

**Check:** `redis.persistentVolume.enabled` (default `false`) and `reminders.provider` (default `Redis`). The bundled Redis has AOF on, but without the persistent volume its data directory is not kept across a pod restart.

**Action:** for production, set `reminders.provider=Postgres` (requires `persistence.provider=Postgres`) and use Kafka or another durable stream provider, or at least enable `redis.persistentVolume.enabled`. Instances whose timers were lost must be moved on through the API. The Compose bundle behaves the same way when its Redis container is recreated.

## Orleans Dashboard returns 404

**Symptom:** `/dashboard` on the admin UI is not found.

**Action:** the dashboard is only mapped when Web authentication is configured (`Authentication__Authority` and `Authentication__ClientId`) or `ASPNETCORE_ENVIRONMENT=Development`. See [Observability — Orleans Dashboard](/fleans/reference/observability/#orleans-dashboard).

## Documented limitations

Known gaps are documented next to the feature they affect:

- [High availability — Known limitations](/fleans/reference/high-availability/#known-limitations): timers up to one minute late during failover, calls waiting out the 30 s response timeout, the single-PostgreSQL layout is not highly available.
- [Streaming — Production-readiness gaps](/fleans/reference/streaming/#production-readiness-gaps): what the Kafka and Azure Queue adapters leave to you.
- [Load testing — known engine issues](/fleans/reference/load-testing/#known-engine-issues): issues the load tests surfaced, such as the literal-only message correlation key.
- [BPMN Support](/fleans/concepts/bpmn-support/): elements that are not, or only partly, implemented.
- [Upgrading](/fleans/guides/upgrading/): before 1.0 there is no upgrade path between releases — a release upgrade means fresh databases.

## Related

- [BPMN Support](/fleans/concepts/bpmn-support/) — full element-level status table.
- [Error Handling](/fleans/guides/error-handling/) — supported error handling patterns.
- [Performance Tuning](/fleans/reference/troubleshooting-performance-tuning/) — throughput knobs.
