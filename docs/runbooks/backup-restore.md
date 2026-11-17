# Runbook: backup and restore

The operator procedure is on the website:
[`website/src/content/docs/guides/backup-restore.md`](../../website/src/content/docs/guides/backup-restore.md)
(published at `/fleans/guides/backup-restore/`), with the Redis helper
[`website/public/samples/fleans-redis-backup.sh`](../../website/public/samples/fleans-redis-backup.sh).
Issue: [#722](https://github.com/nightBaker/fleans/issues/722).

## Keep it in sync

The page lists every store and Redis key that holds state. Update the page, and the script's key
list, in the same PR when you:

- add a table outside `FleanCommandDbContext` / `FleanJournalDbContext`, or a new connection string
  (`fleans-*`);
- add a Redis key that must survive a restore (today: `<ServiceId>/reminders`, `streamprovider-*`,
  `fleans-web-dataprotection-keys`), or rename the stream provider;
- add something that re-creates lost reminders. The page says nothing does: no rehydration on
  activation, and no admin command;
- add a partitioned or otherwise `pg_restore --clean`-hostile object. The page already tells
  operators to restore into an empty database because `--clean` fails on the read-model
  partitions.

## Re-running the end-to-end check

No `dotnet` needed, only Docker and the release bundle:

1. `gh release download v<VER> --repo nightBaker/fleans --pattern 'docker-compose*'`, unzip outside
   the repo, `docker compose up -d`.
2. Deploy and start three fixtures that wait: a user task (`tests/manual/18-user-task`), a message
   catch (`tests/manual/09-message-events/message-catch.bpmn`) and an intermediate timer
   (`tests/manual/08-timer-events/timer-intermediate-catch.bpmn` with `PT5S` raised to a few
   minutes). Use `POST /Definitions/deploy` with `{"bpmnXml": "..."}`, then `POST /Execution/start`.
3. Follow the page's Compose backup, then `docker compose down -v`, then its restore.
4. Check that `GET /Instances/<id>/state` shows each instance still waiting, complete the user task
   (`/UserTasks/<id>/claim`, then `/complete`), send the message (`POST /Execution/message`) and
   wait for the timer. All three finish.
5. Negative control: restore Postgres only. The timer instance stays waiting past its due time.

Last run: v0.14.0, 2026-11-17. All three finished after the restore. A Postgres-only restore left
the timer instance waiting more than 3 minutes past its due time, and the reminder set was empty.
