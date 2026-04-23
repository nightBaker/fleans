# Test Plan: Load Testing Infrastructure (Docker Compose)

## Scenario

Verify that the 5-service Docker Compose stack (redis, postgres, silo-1, silo-2, nginx) starts
correctly and routes HTTP traffic through nginx to the Fleans.Api silos in standalone mode.

## Prerequisites

- Docker Desktop installed and running
- Port 80 is free on the host
- Run from the `tests/load/` directory

## Steps

1. **Build the images**
   ```bash
   cd tests/load
   docker compose build
   ```
   Expected: build completes without errors. Both silos use the same image.

2. **Start the stack**
   ```bash
   docker compose up -d
   ```

3. **Wait for all services to become healthy** (~60s on first run)
   ```bash
   docker compose ps
   ```
   Expected: all 5 services (`redis`, `postgres`, `silo-1`, `silo-2`, `nginx`) show `healthy`.

4. **Verify nginx routes to silos**
   ```bash
   curl -s -o /dev/null -w "%{http_code}" \
     -X POST http://localhost:80/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"nonexistent"}'
   ```
   Expected: HTTP `400` (workflow not deployed → expected domain error, confirms routing works).

5. **Verify round-robin across both silos** — send 4 requests and watch silo logs:
   ```bash
   for i in 1 2 3 4; do
     curl -s -o /dev/null -w "%{http_code}\n" \
       -X POST http://localhost:80/Workflow/start \
       -H "Content-Type: application/json" \
       -d '{"WorkflowId":"nonexistent"}'
   done
   docker compose logs silo-1 | grep "StartWorkflow\|Request" | wc -l
   docker compose logs silo-2 | grep "StartWorkflow\|Request" | wc -l
   ```
   Expected: both silos receive requests (neither count is 0).

6. **Tear down**
   ```bash
   docker compose down -v
   ```
   Expected: all containers stopped and removed, postgres volume deleted.

## Expected Outcomes

- [ ] `docker compose build` completes without errors
- [ ] All 5 services reach `healthy` state
- [ ] `POST /Workflow/start` through nginx returns HTTP `400` (not connection error)
- [ ] Both silos appear in Orleans silo membership (check via silo logs for "Joined" or similar)
- [ ] Round-robin distributes requests across both silos
- [ ] `docker compose down -v` cleans up cleanly
