# 81 — Instance migration

**Feature:** #1069 — move running instances to another version of their process.

**Automated coverage (not repeated here):** `InstanceMigrationTests` in E2E (REST preview / dry run /
batch / retry / rejection; the dialog on the Workflows and instance pages), Application (every wait
kind through the grain, completion and failure after the move, idempotency, replay, the batch
service) and Domain (one test per rejection code, wait rewiring, user-task claims, late callbacks).
What they don't show is how the dialog reads to an operator, a timer the migration adds firing in
real time, and the journal of a migrated instance.

Fixtures (process id `migration-order`):

- `migration-order-v1.bpmn` — `start → review` (user task) `→ v1Finish` (`finishedBy = "v1"`).
- `migration-order-v2.bpmn` — `review` renamed `approve`, a non-interrupting 10-second boundary
  timer `remind` on it, and `v2Finish` (`finishedBy = "v2"`).
- `migration-order-v3-script.bpmn` — `review` becomes a script task (a `type-changed` rejection).

## Prerequisites

```bash
cd src/Fleans
dotnet run --project Fleans.Aspire
```

A SQLite dev database from before this feature is not upgraded: delete it and let the stack
create a new one.

## Steps

1. Deploy `migration-order-v1.bpmn` (editor or `POST /Definitions/deploy`) and start three
   instances from **Workflows → Start**.
   - [ ] Each instance waits in `review`.
2. Deploy `migration-order-v2.bpmn`. On **Workflows**, expand `migration-order`, click **Migrate** on
   the `v1` row.
   - [ ] The dialog targets `v2` and says `3 running instance(s) on v1.`
   - [ ] The `review` row is highlighted with `unmapped-activity`.
3. Click **Dry run** without changing the mapping.
   - [ ] `3 rejected`, each naming `unmapped-activity (review)`. Nothing changed (the instances
     still show `v1`).
4. Map `review` to `approve (UserTask)`, click **Dry run**, then **Migrate**.
   - [ ] The dry run says `3 can be migrated`; the migration says `3 migrated`.
   - [ ] **Migrate** again says `No running instances left on v1.`
5. Open one migrated instance.
   - [ ] The diagram is v2's, `approve` is active, and the instance id is unchanged.
   - [ ] The journal (and **Debug**) has an `Instance migrated` step: `Migrated from v1 to v2 · review → approve`.
   - [ ] About 10 seconds after the migration, `remind` completes (the timer v2 added was armed);
     `approve` stays active.
6. Claim and complete `approve` (user tasks list or REST).
   - [ ] The instance completes with `finishedBy = "v2"`; the history still shows `start` from v1.
7. Start an instance on v1, deploy `migration-order-v3-script.bpmn`, open the instance and click
   **Migrate** (target `v3`), then **Migrate**.
   - [ ] The report says `1 rejected` with `type-changed (review)`; the instance stays on v1 and
     completes there after `review`.
8. Switch the interface to Russian (**Settings → Language**) and open the dialog again.
   - [ ] Labels, outcomes and buttons are in Russian; rejection codes and element ids are not translated.
