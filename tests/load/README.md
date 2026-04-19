# Fleans Load Testing Infrastructure

Docker Compose stack for multi-silo load testing:

```
k6 / curl → nginx:80 → [round-robin] → silo-1:8080
                                      → silo-2:8080
                              │
                       postgres:5432
                       redis:6379
```

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) ≥ v24
- [k6](https://k6.io/docs/get-started/installation/) ≥ v0.51 (for running load scenarios)

## Build

Run from the `tests/load/` directory:

```bash
docker compose build
```

The build context is `src/Fleans/` — Docker resolves all project-to-project references during `dotnet restore`.

## Run

```bash
docker compose up -d
```

Wait for all 5 services to become healthy (~60s on first run):

```bash
docker compose ps
```

All services should show `healthy`. nginx becomes healthy only after both silos are healthy.

## Verify

Send a request through the nginx load balancer:

```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:80/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"nonexistent"}'
```

Expected: `400` (no workflow deployed — confirms nginx → silo routing works end-to-end).

## Deploy BPMN Fixtures

Upload a BPMN fixture before running load scenarios:

```bash
curl -X POST http://localhost:80/Workflow/upload-bpmn \
  -F "file=@path/to/fixture.bpmn"
```

## Run k6 Scenarios

From the `tests/load/` directory:

```bash
k6 run scripts/setup.js          # deploy fixtures
k6 run scripts/mixed-workload.js # main load scenario
```

## Orleans Dashboard

The Orleans dashboard is available on each silo directly (not proxied through nginx):

- silo-1: `http://localhost:8081` (map port 8080 of silo-1 to 8081 if needed via `ports:` in compose)

## Tear Down

```bash
docker compose down -v
```

The `-v` flag removes the postgres data volume for a clean slate on the next run.

## Configuration

| Environment Variable | Default | Description |
|---|---|---|
| `FLEANS_STANDALONE` | `true` | Activates standalone Orleans config (clustering + storage outside Aspire) |
| `ConnectionStrings__orleans-redis` | `redis:6379` | Redis connection for Orleans clustering and PubSubStore |
| `ConnectionStrings__fleans` | postgres connection | PostgreSQL connection string |
| `Persistence__Provider` | `Postgres` | Persistence provider (always Postgres in this stack) |

PostgreSQL is tuned for write-heavy load testing (`synchronous_commit=off`, `shared_buffers=256MB`). Do not use these settings in production.
