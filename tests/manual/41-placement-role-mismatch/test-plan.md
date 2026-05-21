# Manual Test Plan — #41 Placement Role Mismatch

Verifies #457: `PlacementRoleAssertion` fails fast at silo startup when `Fleans:Role` is incompatible with the placement attribute of a registered grain.

## Prerequisites

- Aspire publish topology built: `aspire publish --project Fleans.Aspire -t docker-compose -o out/compose`
- `docker compose` available
- Aspire stack NOT currently running (or use a separate `--project-name` to isolate)

## Steps

### 1. Baseline — Worker role boots cleanly

```bash
cd out/compose
docker compose up -d fleans-worker
docker logs fleans-worker --tail 50 | grep -E "PlacementRoleAssertion|Fleans:Role"
```

**Expected:** A log line at Information level with EventId `11200`:
```
PlacementRoleAssertion: Fleans:Role='Worker' on silo 'worker-...' — checked N placement-attributed grain(s); no violations.
```
The container stays `Up`. N is at least 2 (`ScriptExecutorGrain` + `ConditionExpressionEvaluatorGrain`).

### 2. Misconfig — set `Fleans:Role=Core` on the worker host

Edit the compose override file to set the worker container's environment:

```yaml
services:
  fleans-worker:
    environment:
      Fleans__Role: "Core"
```

Restart:
```bash
docker compose up -d fleans-worker --force-recreate
sleep 5
docker compose ps fleans-worker
```

**Expected:** `fleans-worker` shows `Exited (1)` (or similar non-zero exit).

```bash
docker logs fleans-worker --tail 20
```

**Expected:** an exception message containing:
- `Plugin grain 'Fleans.Worker.Scripts.ScriptExecutorGrain'`
- `carries [WorkerPlacement]`
- `Fleans:Role is 'Core'`
- `'Worker' or` / `'Combined'`
- `Fleans:Role=Worker`

A structured log line at Error level with EventId `11201` should appear before the exception, naming the grain type and the mismatch.

### 3. Recovery — restore `Fleans:Role=Worker`

Revert the override:
```yaml
services:
  fleans-worker:
    environment:
      Fleans__Role: "Worker"
```

```bash
docker compose up -d fleans-worker --force-recreate
sleep 10
docker compose ps fleans-worker
```

**Expected:** Container is `Up`, no exception in logs, baseline EventId 11200 message returns.

## Pass criteria

- Step 1 passes (baseline boot, EventId 11200 in logs).
- Step 2 fails the container with the verbatim AC2 message naming a `[WorkerPlacement]` grain and the wrong-role `Core`.
- Step 3 recovers cleanly.

## Cleanup

```bash
docker compose down
```
