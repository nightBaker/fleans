# Clustering (Orleans membership)

The membership table decides which silo is alive. Its load is tiny (one row per silo, an
`IAmAlive` write every 30 s; probes go silo-to-silo), so the store is chosen for **consistency and
availability, not throughput**: Orleans updates the silo list and the table version atomically, and
while the table is unreachable no silo can be declared dead and no new silo can join (#885).

All wiring lives in `Fleans.ServiceDefaults/Clustering/FleansClusteringExtensions.cs`.

## Provider: `Fleans:Clustering:Provider`

| Value | Store | Connection | When |
|---|---|---|---|
| `Redis` (default) | `Microsoft.Orleans.Clustering.Redis` | `ConnectionStrings:orleans-redis` | Dev, small installs. Redis replication is asynchronous — a failover can drop an acknowledged membership write. |
| `Postgres` | `Microsoft.Orleans.Clustering.AdoNet` (`Npgsql`) | `ConnectionStrings:fleans-cluster` (own database) or else `ConnectionStrings:fleans` | **Production.** Durable, strongly consistent; run Postgres highly available (Patroni). Requires `Persistence:Provider=Postgres`. |

Matched case-insensitively; an unknown value throws `ArgumentException`, a missing connection
string or `Postgres` without Postgres persistence throws `InvalidOperationException` — at startup.

**Every silo and client of one cluster must use the same provider**: the membership table is also
the clients' gateway list.

| Host | Call |
|---|---|
| `Fleans.Api`, `Fleans.WorkerHost` (silos) | `siloBuilder.AddFleansClustering(configuration)` |
| `Fleans.Web`, `Fleans.Mcp`, `Fleans.Api` with `Fleans:Role=Api` (clients) | `clientBuilder.AddFleansClustering(configuration)` |

**Aspire.** The AppHost stays on Redis: `WithClustering(redis)` injects Orleans' own
`Orleans:Clustering` section, which Orleans applies by itself. When that section exists,
`AddFleansClustering` registers nothing (no second membership table), and
`Fleans:Clustering:Provider=Postgres` next to it throws — configure membership in one place.

**Schema.** With `Postgres`, `EnsureDatabaseSchemaAsync` runs the vendored
`Resources/PostgreSQL-Clustering.sql` (Orleans v10.4.0, see `LICENSE-orleans-vendor`) under the
migration advisory lock, before the silo starts — next to the app tables, or in the
`fleans-cluster` database. Each Orleans script is skipped when its main table exists
(`orleansquery`, `orleansreminderstable`, `orleansmembershiptable`), so reminders and membership can
share one database. Hosts that never call `EnsureDatabaseSchemaAsync` rely on an engine silo having
created it; until then their membership init fails and they restart.

## Kubernetes hosting: `Fleans:Kubernetes:Enabled`

`true` → silos also call Orleans' `UseKubernetesHosting()` (`Microsoft.Orleans.Hosting.Kubernetes`).
It does **not** replace the membership provider. At startup a silo lists the pods labelled with its
`orleans/serviceId` / `orleans/clusterId` and declares every active silo without a pod dead; at
runtime two silos watch the pods, so a deleted pod leaves the membership table immediately instead
of after the probe window (the stale-silo window seen in the v0.1.0 load test).

What it needs from the pod (the Helm chart provides all of it):

- RBAC on `pods`: `get`, `list`, `watch`, `patch` (it patches its own `orleans/*` labels). `delete`
  only with `DeleteDefunctSiloPods`, which Fleans leaves off.
- `POD_NAME`, `POD_NAMESPACE`, `POD_IP` from the downward API.
- `Orleans:ServiceId` / `Orleans:ClusterId` equal to the pod labels (or `ORLEANS_SERVICE_ID` /
  `ORLEANS_CLUSTER_ID`).

**Silo name = pod name.** Kubernetes hosting matches membership rows to pods by silo name, so the
silo name must be the pod name. Fleans placement reads the role from the silo-name prefix
(`core-`, `worker-`, `combined-` — see [placement-and-roles.md](placement-and-roles.md)),
so **the pod name must start with the role**. `FleansClusteringExtensions.ResolveSiloName` returns
the pod name and throws at startup when the prefix is missing; the chart names silo Deployments
`<role>-<fullname>`. Without Kubernetes hosting the silo name stays `{role}-{machine}-{guid}`.
Custom-task workers are not silos, so none of this applies to them.

## Ring guard: a joining silo reported dead to itself (#902)

`AddFleansClustering` also calls `AddConsistentRingSelfStatusGuard()`, a workaround for an Orleans
10.4 bug. A silo that starts while the membership table is empty — on a fresh cluster, every silo
except the first one to write — keeps a synthetic local `Created` entry, because Orleans rejects the
empty read. When its next read, just before it writes itself as `Joining`, finds another silo but not
itself, the silo-status listeners get `Dead` for the local silo. The consistent ring takes that as
"this silo is stopping" and excludes the silo from routing for its whole life. Grain-service calls
from that silo for keys it owns then go to a neighbour. For reminders this means a `TimerCallbackGrain`
reminder registered on the neighbour, which since Orleans 10.4 only stores it. The owner loads it at
its next `RefreshReminderListPeriod`, so the BPMN timer fires up to 5 minutes late. With 3 Core
replicas starting together, 2 were affected and about 12 % of timers fired late.

The guard swaps the ring's `ISiloStatusOracle` (and only the ring's) for one that drops a terminating
notification about the local silo while the local silo is not terminating. A real shutdown or
self-kill updates the local status first, so the ring still sees it. A dropped notification logs
EventId 11400 (Warning) once per affected silo start. `ConsistentRingSelfStatusGuardTests` reproduces
the start sequence. Its control test fails once Orleans fixes the bug, which means the guard can go. The
control counts registrations that reach a silo outside its range (Orleans `LocalReminderService` warning
102910), not reminders that fire late: the joiner loads its reminder range in the background, and a load
that lands after the reminders are stored fires them on time despite the misrouting (#954).
The guard finds the ring registration by its internal type name and fails startup if an Orleans
upgrade moves it.

## Stale Active silos: `StaleSiloSweeper` (#1098)

Before a silo turns Active, Orleans 10.4 probes every `Active` silo in the table and retries for
`MaxJoinAttemptTime` (5 min) until all answer; there is no switch to skip it. A silo that crashed,
or ran out of stop time before leaving the table, stays `Active` in a persistent table, and only
Active silos vote a silo dead. When the whole cluster went down — always the case for a single
`Combined` silo — nothing removes that row, so every restart at a new address fails the check and
hosting exits after 5 minutes. A restart at the *same* `IP:port` was never affected: Orleans retires
older generations of its own endpoint.

`StaleSiloSweeper` (registered by `AddFleansClustering`, any provider) runs while the
`ValidateInitialConnectivity` stage holds: every 5 s it reads the table and declares dead each other
`Active` silo whose heartbeat (`IAmAliveTime`, or `StartTime` if later) is older than
`IAmAliveTablePublishTimeout × NumMissedTableIAmAliveLimit` (30 s × 3 = 90 s). That is the window
after which Orleans itself stops counting a silo when it tallies death votes, and a live silo
refreshes its heartbeat every 30 s, so only silos that are gone (or cut off from the table that long)
are removed. Orleans' retry loop rereads the table and the join passes. It stops at `BecomeActive`.
Logs: EventId 11410 per silo declared dead, 11411 when a sweep fails. Covered by
`StaleSiloSweeperTests`; the end-to-end restart is `tests/manual/82-stale-silo-restart`.

A restart right after a crash therefore waits up to ~90 s. `Fleans:Clustering:Membership` binds onto
Orleans' `ClusterMembershipOptions` (after Orleans' defaults, so configuration wins); a single-silo
install can shorten the wait with
`Fleans__Clustering__Membership__IAmAliveTablePublishTimeout=00:00:10` (window 30 s). Lowering it adds
heartbeat writes to the table — one per silo per period.

## Redis is optional: `AddFleansRedisClient`

Every host registers the keyed `orleans-redis` multiplexer — and with it Aspire's Redis health
check, which gates `/health` — through `builder.AddFleansRedisClient(FleansHostKind.Silo | Client)`
(`Fleans.ServiceDefaults/FleansRedisExtensions.cs`). It registers only when
`FleansRedisExtensions.IsRedisRequired` finds a Redis-backed provider:

- clustering — `Orleans:Clustering:ProviderType` when that section exists (Aspire), else `Fleans:Clustering:Provider`;
- silos only: `Fleans:Reminders:Provider` and `Fleans:Streaming:Provider`.

Unset counts as Redis (all three default to it). So a host with clustering and reminders on
Postgres and streams on Kafka / Azure Queue never connects to Redis and its `/health` does not
depend on it. The helper never throws; each provider's own resolver fails fast on a bad value.
`Fleans.Web` persists its Data Protection keys to Redis only when the client is registered;
otherwise they stay in the process (one Web replica with OIDC). **Adding a Redis-backed
provider means extending `IsRedisRequired`** and `FleansRedisExtensionsTests`.

## Graceful shutdown

On SIGTERM the host stops the silo gracefully (leave membership, hand off grains and its reminder
range) within .NET's `HostOptions.ShutdownTimeout` (config key `shutdownTimeoutSeconds`, default 30 s).
The chart sets `terminationGracePeriodSeconds` (default 60) on silo pods and
`shutdownTimeoutSeconds` 10 s lower, so the silo finishes before SIGKILL. Docker's default stop
timeout is 10 s, shorter than the host's 30 s: outside Helm give containers `--stop-timeout` /
`stop_grace_period` above `shutdownTimeoutSeconds`, or a slow stop is killed and leaves the silo
`Active` in the table (the sweeper above removes it at the next start, after the 90 s window).

## Failover: tested, measured, documented (#723)

`SiloFailoverTests` (Application) kills or stops a secondary silo of a 3-silo `TestCluster` that
hosts an instance waiting on a task and a timer, and checks that the workflow completes on a
survivor with each activity exactly once. Two traps in that test cluster:

- **Never kill the primary.** The test cluster's membership table lives on the primary silo, so
  killing it ends the cluster instead of failing over (the trap `EventStoreDurabilityTests` notes).
- **The in-memory reminder table is a grain.** It can live on the silo being killed and vanish with
  it, so the test swaps in a table shared by every silo of the process (`SharedReminderTable`),
  standing in for the Postgres / Redis providers.

The real-cluster drill is `tests/manual/79-silo-failover` (load-test stack, `docker kill` /
`docker stop`); its recorded numbers are on the website page `reference/high-availability.md`.
Re-run it and update both when membership, reminders, `TimerCallbackGrain` or graceful shutdown
change.

## Not covered here

- Stream pub-sub state: there is none, since streams are implicit-only ([streaming.md](streaming.md#implicit-subscriptions-only-no-pubsubstore), #911).
- Reminders: [reminders.md](reminders.md).
