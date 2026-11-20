# 82 — Stale silo restart

**Feature:** #1098 — a silo that is gone but still `Active` in a persistent membership table no
longer blocks the next silo's join. Orleans 10.4 probes every Active silo before it lets a silo
join; with the whole cluster down nobody could declare the old silo dead, so a single `Combined`
silo restarted at a new address failed every start. `StaleSiloSweeper` declares such a silo dead
once it has watched its heartbeat stay unchanged for longer than
`IAmAliveTablePublishTimeout × NumMissedTableIAmAliveLimit` (90 s).

**Automated coverage (not repeated here):** `StaleSiloSweeperTests` (unchanged heartbeat past the window,
an old timestamp alone is not enough, live heartbeats, a failed read restarts the watch, the
configurable window, a lost write) and `FleansClusteringExtensionsTests` (sweeper registered,
`Fleans:Clustering:Membership` bound). What they cannot show: Orleans' real join check against a
real table. That is this plan.

No BPMN fixture: the engine's `/health` is the signal.

## Prerequisites

Docker and the .NET SDK. From the repo root:

```bash
docker run -d --name pg1098 -e POSTGRES_PASSWORD=p -e POSTGRES_DB=fleans -p 55432:5432 postgres:16-alpine
dotnet build src/Fleans/Fleans.Api/Fleans.Api.csproj
export Fleans__Role=Combined Persistence__Provider=Postgres Fleans__Clustering__Provider=Postgres \
  Fleans__Reminders__Provider=Postgres Fleans__Streaming__Provider=Memory \
  Orleans__ServiceId=fleans Orleans__ClusterId=fleans \
  ConnectionStrings__fleans="Host=localhost;Port=55432;Database=fleans;Username=postgres;Password=p" \
  ASPNETCORE_URLS=http://localhost:28480
alias engine='dotnet src/Fleans/Fleans.Api/bin/Debug/net11.0/Fleans.Api.dll'
alias members='docker exec pg1098 psql -U postgres -d fleans -c "select address, generation, status, iamalivetime from orleansmembershiptable order by generation"'
```

A restart at the same `IP:port` was never affected (Orleans retires older generations of its own
endpoint), so each case moves the stale row to an unreachable address, as Docker does when it hands
the new container another IP.

## A. Crash, then restart right away

1. `engine &` — wait until `curl -sf localhost:28480/health` succeeds.
2. `kill -9 %1`, then `members` — expect one row, status `3` (Active).
3. `docker exec pg1098 psql -U postgres -d fleans -c "update orleansmembershiptable set address='10.255.255.1' where status=3"`
4. `engine &` and time until `/health` succeeds.
5. Expect: healthy within ~100 s (recorded 102 s); the log has one Warning
   `StaleSiloSweeper[11410] Declared stale silo S10.255.255.1:… dead`; `members` shows that row at
   status `6` and the new silo at `3`. Before the fix: `Failed to get ping responses from 1 of 1
   active silos` every 5 s and `Hosting failed to start` after 5 minutes.

## B. Shorter window

1. Repeat A with `Fleans__Clustering__Membership__IAmAliveTablePublishTimeout=00:00:10` set for
   step 4.
2. Expect: healthy within ~40 s (recorded 41 s); 11410 says `more than 00:00:30`.

## C. Long-dead row

1. Leave a row from A step 3 Active for more than 90 s (kill the engine, rewrite the address, wait).
2. `engine &`
3. Expect: healthy within ~25 s (recorded 23 s). Orleans evicts this one itself — a peer already
   stale at the first table read is watched and voted out by the joining silo (`Evicting …`,
   no 11410). This case guards against the sweeper getting in the way.

## D. Graceful stop

1. With the engine running, `kill -TERM %1`.
2. Expect: `members` shows its row at status `6`; the next start is healthy within seconds with no
   11410.

## E. Two live silos are left alone

1. Start a second engine on other ports
   (`ASPNETCORE_URLS=http://localhost:28481 Orleans__Endpoints__SiloPort=11112 Orleans__Endpoints__GatewayPort=30001 engine &`)
   next to a running one.
2. Expect: both healthy, both rows `3` after several minutes, no 11410 in either log.

Cleanup: `docker rm -f pg1098`.
