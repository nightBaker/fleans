---
title: Backup and restore
description: What Fleans keeps in each store, how to back up the databases and the Redis keys together, the restore procedure for the Docker Compose bundle and the Helm chart, how to check the result, and what a restore cannot bring back.
---

A Fleans installation keeps its state in more than one store. The databases hold workflow
instances, but pending BPMN timers live in the **reminder store**, which is Redis by default. A
backup of the database alone restores every instance, but the timers are gone: an instance that
waits on a timer then waits forever, and nothing reports it. Back up the reminders together with
the databases.

**Restore with the same Fleans version that took the backup.** Before 1.0 there is no upgrade
path between releases: the schema, the journal format and the snapshots change in place. See
[Upgrading](/fleans/guides/upgrading/).

## What to back up

| Store | Holds | Back up? |
|---|---|---|
| Main database (`ConnectionStrings:fleans`) | Deployed process definitions; the state of user tasks, message and signal subscriptions, start-event listeners, timer start schedules, the custom-task catalog, deployment requests and message de-duplication; the read model (instance lists, completed instances). Also the journal, unless it has its own database. | **Yes.** Most of this exists nowhere else. |
| Journal database (`ConnectionStrings:fleans-journal`, only when set) | `WorkflowEventBatches`, the event journal and source of truth of every workflow instance, and `WorkflowSnapshots` | **Yes.** |
| Read replica (`ConnectionStrings:fleans-query`) | A copy of the main database | No. Rebuild it from the main database. |
| Reminder store | One reminder per pending BPMN timer and per timer start event. Redis key `<ServiceId>/reminders` (default), or the `orleansreminderstable` table with `Fleans:Reminders:Provider=Postgres` (in the main database, or in `ConnectionStrings:fleans-reminders`) | **Yes.** Nothing re-creates a lost reminder. |
| Stream queues | Hand-offs that are not delivered yet: custom tasks, signals, read-model updates. Redis keys `streamprovider-*`, or Kafka topics / Azure queues | Recommended with Redis. They are short-lived; see [consistency](#consistency). |
| Cluster membership | Which silos are alive. Redis key `<ServiceId>/members/<ClusterId>` or the `orleansmembershiptable` table | No. The silos write it again when they start. |
| Data Protection keys | The admin UI's sign-in cookie keys. Redis key `fleans-web-dataprotection-keys`, only with authentication on | Optional. Without them, users sign in again. |
| Camunda external tasks (Helm `plugins.enabled`) | The plugins workers' task table, database `camunda_external_tasks` on the bundled Postgres | **Yes**, when you use the plugins workers. |
| Configuration | `.env`, Helm values and Secrets: passwords, `Orleans__ServiceId`, `Orleans__ClusterId` | **Yes.** Reminders are stored under the ServiceId, so the restored installation must use the same one. |

Snapshots and the read model are derived from the journal, but there is no command that rebuilds
them for all instances. An instance repairs its own read-model row when it is next activated.
Back up whole databases and don't leave tables out.

The **Docker Compose bundle** runs Postgres (journal in the main database) and a Redis that holds
reminders, streams and membership. That Redis has no volume, so `docker compose down` deletes its
data. The **Helm chart** defaults are the same: bundled Postgres, Redis reminders and streams,
and no Redis volume (`redis.persistentVolume.enabled=false`). With
`reminders.provider=Postgres` and `clustering.provider=Postgres`, everything you must back up is
in Postgres.

## Consistency

The stores have no shared transaction, and a separate backup of each one is taken at a different
moment. Anything that happens between the two backups is half in the backup:

- A timer that starts after the reminders were saved but before the database: the restored
  instance waits on a timer that has no reminder.
- A timer that fires after the database was saved but before the reminders: the restored instance
  still waits, and its reminder is gone.

Both leave an instance waiting with no error. To avoid this:

- **Stop the Fleans workloads during the backup** (cold backup). Keep the databases and Redis
  running. This is the procedure below and the one that was tested. The downtime is the time a
  `pg_dump` takes.
- Or keep reminders in the same Postgres database as the journal (`reminders.provider=Postgres`
  without a dedicated reminders database). One `pg_dump` is one consistent snapshot of both.
  Instances keep running during the backup, but a timer that starts in the moment the snapshot is
  taken may still be missed.

On managed Postgres, a point-in-time restore of every Fleans database to the same timestamp
replaces `pg_dump`.

After a restore, Fleans delivers again what was in the restored stream queues. Delivery is
at-least-once and the engine ignores a duplicate. Work that happened after the backup can happen a
second time, for example a custom task that an external worker had already completed.

## Back up

Download the Redis helper script next to your Compose bundle or `kubectl` workstation:
[**fleans-redis-backup.sh**](/fleans/samples/fleans-redis-backup.sh). Inside the Redis container,
it prints a `redis-cli` command file with a `RESTORE` line for the reminders, the stream queues and
the Data Protection keys. It leaves out the membership. Its argument is the ServiceId
(`fleans` unless you changed `CLUSTER_SERVICE_ID` or `clustering.serviceId`).

### Docker Compose

Run these commands from the bundle directory:

```bash
mkdir -p backup

# 1. Stop the Fleans workloads; Postgres and Redis keep running.
docker compose stop fleans-management fleans-mcp fleans-core fleans-worker

# 2. Postgres (custom format, for pg_restore).
docker compose exec -T postgres sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -U postgres -d fleans -Fc' > backup/fleans.dump

# 3. Redis keys: reminders, streams, Data Protection keys.
docker compose exec -T orleans-redis sh -s -- fleans < fleans-redis-backup.sh > backup/redis-keys.txt

# 4. The .env file (passwords, CLUSTER_SERVICE_ID).
cp .env backup/.env

# 5. Start the Fleans workloads again.
docker compose start fleans-core fleans-worker fleans-management fleans-mcp
```

The bundle's Postgres requires a password on local connections too, so `pg_dump` needs
`PGPASSWORD`.

### Helm

The commands below use the release name `fleans` in the namespace `fleans`, which gives the
StatefulSets `fleans-postgres` and `fleans-redis`.

```bash
mkdir -p backup

# 1. Write down the replica counts, then stop every Fleans Deployment of the release.
#    The Postgres and Redis StatefulSets keep running.
kubectl -n fleans get deploy -l app.kubernetes.io/instance=fleans
kubectl -n fleans scale deploy -l app.kubernetes.io/instance=fleans --replicas=0

# 2. Postgres.
kubectl -n fleans exec fleans-postgres-0 -- sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > backup/fleans.dump
# Only with plugins.enabled=true:
kubectl -n fleans exec fleans-postgres-0 -- sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -U "$POSTGRES_USER" -d camunda_external_tasks -Fc' > backup/camunda.dump

# 3. Redis keys (skip with reminders.provider=Postgres and a non-Redis stream provider).
kubectl -n fleans exec -i fleans-redis-0 -- sh -s -- fleans < fleans-redis-backup.sh > backup/redis-keys.txt

# 4. The release values (they hold clustering.serviceId).
helm -n fleans get values fleans -o yaml > backup/values.yaml

# 5. Scale every Deployment back to its count from step 1.
kubectl -n fleans scale deploy/core-fleans --replicas=1   # and so on for each Deployment
```

**Managed or external Postgres.** Run `pg_dump "<connection string>" -Fc` from a host that can
reach the server, with a `pg_dump` at least as new as the server. Dump every Fleans database: the
main one, plus `fleans-journal` and `fleans-reminders` when they are set. Or take a provider
snapshot or point-in-time restore of all of them at the same moment. On a **Citus** journal, back
up the whole Citus cluster with your provider's tooling.

### SQLite (single host)

SQLite is not supported by the Compose bundle or the Helm chart. If you run a Fleans host with
SQLite, the database file holds the main data and the journal (unless `fleans-journal` is set).
The file uses WAL mode: stop the host and copy the file with its `-wal` and `-shm` files, or copy it
online with `sqlite3 <file> ".backup '<copy>'"`. Reminders are still in Redis, so back up the
Redis keys as well.

## Restore

The Fleans workloads must not run while you restore. Start them only after the database and the
Redis keys are back.

### Docker Compose

This procedure was tested end to end with the Compose bundle of release 0.14.0: a backup,
`docker compose down -v`, then a restore. It brought back instances waiting on a user task, a message and a timer. The user task
and the message could be completed, and the timer fired on time. In the same test, restoring
Postgres without the Redis keys left the timer instance waiting minutes past its due time, with
no error.

From a bundle directory of the **same version**. The ServiceId must match the backup; copying the
backed-up `.env` is the simplest way:

```bash
cp backup/.env .env

# 1. Start only Postgres and Redis. Postgres creates an empty `fleans` database.
docker compose up -d --wait postgres orleans-redis

# 2. Postgres, before any Fleans workload has created the schema.
docker compose exec -T postgres sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -U postgres -d fleans --exit-on-error' < backup/fleans.dump

# 3. Redis keys. Each line prints OK.
docker compose exec -T orleans-redis sh -c \
  'redis-cli -a "$REDIS_PASSWORD" --no-auth-warning' < backup/redis-keys.txt

# 4. Start everything.
docker compose up -d
```

### Helm

Install the **same chart version** with the backed-up values (`helm install fleans
./fleans-<VER>.tgz -n fleans -f backup/values.yaml`), or use the existing release. Then stop the
Fleans Deployments, replace the data and start them again:

```bash
# 1. Stop the Fleans workloads.
kubectl -n fleans scale deploy -l app.kubernetes.io/instance=fleans --replicas=0

# 2. Postgres: replace the database a fresh install has already migrated with an empty one,
#    then restore into it.
kubectl -n fleans exec fleans-postgres-0 -- sh -c \
  'export PGPASSWORD="$POSTGRES_PASSWORD"; psql -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 \
     -c "DROP DATABASE $POSTGRES_DB WITH (FORCE)" -c "CREATE DATABASE $POSTGRES_DB"'
kubectl -n fleans exec -i fleans-postgres-0 -- sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --exit-on-error' \
  < backup/fleans.dump

# 3. Redis keys. REPLACE overwrites the keys the fresh install has created.
kubectl -n fleans exec -i fleans-redis-0 -- redis-cli < backup/redis-keys.txt

# 4. Scale back to the replica counts you wrote down.
kubectl -n fleans scale deploy/core-fleans --replicas=1   # and so on
```

Database passwords are not part of a `pg_dump`, so a new install with a new generated password
works. With `plugins.enabled=true`, restore `backup/camunda.dump` into `camunda_external_tasks` the
same way.

Don't use `pg_restore --clean` on a database that already has the schema: it fails on the
partitioned read-model tables (`cannot drop inherited constraint`). Restore into an empty database.

For managed Postgres, create an empty database for each Fleans database and restore into it with
`pg_restore -d "<connection string>" --exit-on-error` (add `--no-owner` when the database role
differs), or use the provider's restore, while the Fleans Deployments are at zero.

The `RESTORE` payloads are tied to the Redis major version. Restore into the same Redis image that
took the backup (the bundle uses `redis:8.6`, the chart `redis:7-alpine`).

## Verify

1. `GET /health` on the API answers `Healthy`.
2. Instances you know were waiting are back: `GET /Instances/<id>/state` shows them not completed,
   with the same `activeActivities`. Reading an instance's state activates it, and activation
   re-sends a script task that was running at backup time.
3. The reminders are back: their count matches the number of instances waiting on a timer, plus
   one per process with a timer start event.

   ```bash
   docker compose exec -T orleans-redis sh -c \
     'redis-cli -a "$REDIS_PASSWORD" --no-auth-warning ZCARD fleans/reminders'
   # Postgres reminders: SELECT count(*) FROM orleansreminderstable;
   ```

4. `GET /UserTasks` lists the open user tasks.
5. Finish one waiting instance: complete a user task, send its message, or wait for its timer.

## What is not recoverable

- **Everything after the backup.** Instances started, steps completed and processes deployed after
  the backup are gone. Clients that got a success response for them are not told.
- **Pending timers, when the reminders were not restored.** The instances wait forever, without an
  error. Nothing in Fleans re-registers a lost reminder. For a timer start event, disabling and
  enabling the process (`POST /Definitions/disable`, then `/enable`) registers it again and starts
  its schedule over. A waiting intermediate timer or boundary timer cannot be re-armed.
- **Stream events that are not in the backup.** Hand-offs published after the Redis backup, and
  anything in Kafka or Azure Queue Storage, which this procedure does not back up. A custom task
  whose hand-off is lost stays waiting for its worker.
- **Half of a change made between two backups**: see [consistency](#consistency).
- **Sign-in sessions**, if the Data Protection keys were not restored.
- **A backup from another Fleans version.** Restore it with the version that took it.
