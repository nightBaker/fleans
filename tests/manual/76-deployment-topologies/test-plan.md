# 76 — Deployment topologies: single PostgreSQL and the scalable layout

**Feature:** the two layouts in the website guides — *Simplest start: one PostgreSQL*
(`website/src/content/docs/guides/topology-single-postgres.md`) and *Recommended for scale*
(`website/src/content/docs/guides/topology-scalable.md`) — run with released images, and the
`fleans-plugins` host joins each of them (Postgres membership/reminders; Memory or Kafka streams).

**Automated coverage (not repeated here):** `helm-lint` workflow (chart renders, `plugins.enabled`),
`PluginHostPlacementTests` (plugin silo named after its pod under Kubernetes hosting),
`FleansRedisExtensionsTests` (no Redis client without a Redis provider). The compose runs below are not
automated.

Fixtures: `topology-smoke.bpmn` (process `topology-smoke`: script → 20 s timer → message `go`
correlated on `orderId` → end) and `topology-rest-call.bpmn` (process `topology-rest`: REST Caller
`GET =url` → script copies `status` → end).

## Prerequisites

- Docker; for a pre-release run, local images tagged like the release (`fleans-api`, `fleans-web`,
  `fleans-worker`, `fleans-plugins`) and the compose variables `FLEANS_*_IMAGE` pointing at them.
- Copy the `docker-compose.yml` from each guide into its own empty directory.

## A. Single PostgreSQL

1. `docker compose up -d --wait` — `postgres`, `fleans` healthy; `fleans-web`, `fleans-plugins` up.
   No Redis or Kafka container exists.
2. Deploy `topology-smoke.bpmn` (`POST /Definitions/deploy`), start with
   `{"WorkflowId":"topology-smoke","Variables":{"orderId":"a1"}}`.
3. Within the 20 s timer run `docker compose restart fleans`; wait for `/health` = 200.
4. After the timer is due, `POST /Execution/message` `{"MessageName":"go","CorrelationKey":"a1"}`.
   **Expect:** `GET /Instances/{id}/state` → `isCompleted: true`, `isFailed: false`, variable `x = 7`
   (the timer survived the restart).
5. Deploy `topology-rest-call.bpmn`, start with `{"WorkflowId":"topology-rest","Variables":{"url":"http://fleans:8080/health"}}`.
   **Expect:** completed, not failed, `status = "200"`, `copied = "200"` (REST Caller ran on the
   plugin host over Memory streams).
6. Admin UI on `http://localhost:8081` lists both instances; *Custom tasks* shows `rest-call`.

## B. Scalable layout

1. `docker compose up -d --wait` — 2 × `fleans-core` healthy, `citus-setup` exited 0, every other
   service up. No Redis container exists.
2. Repeat A.2 and A.4 (skip the restart) through `http://localhost:8080` (`fleans-api`, Api role).
   **Expect:** completed.
3. Repeat A.5 with `"url":"http://fleans-api:8080/health"`. **Expect:** completed, `status = "200"`
   (plugin host on Kafka streams).
4. `docker compose exec citus-coord psql -U postgres -d journal -c "SELECT logicalrelid FROM pg_dist_partition"`
   **Expect:** the journal and snapshot tables are distributed.

Last run: 2026-10-26 against the 0.9.0 release branch (local images) — A and B.1–B.3 PASSED; B.4 not run yet.
