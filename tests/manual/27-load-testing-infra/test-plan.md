# Load Testing Infrastructure — Manual Test Plan

## Scenario

Verify the Aspire Docker compose publisher generates a working multi-silo cluster with nginx load balancer.

## Prerequisites

- .NET SDK installed
- Docker Desktop running
- k6 installed (for load test scripts)

## Steps

### 1. Generate Docker Compose stack

```bash
dotnet run --project src/Fleans/Fleans.Aspire -- --publisher docker-compose --output-path tests/load/generated
```

**Expected:** `tests/load/generated/` contains `docker-compose.yml` and per-project Dockerfiles.

### 2. Start the stack

```bash
cd tests/load/generated && docker compose up -d
```

**Expected:** All services start (redis, postgres, 2x fleans-core replicas, nginx).

### 3. Verify services are healthy

```bash
docker compose ps
```

**Expected:** All containers show `Up` / `healthy` status.

### 4. Test API endpoint via nginx

```bash
curl -X POST http://localhost:80/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"nonexistent"}'
```

**Expected:** HTTP 400 response (workflow not deployed, but proves nginx -> silo connectivity).

### 5. Verify round-robin load balancing

Send 4 requests and check logs:

```bash
for i in 1 2 3 4; do
  curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:80/Workflow/start \
    -H "Content-Type: application/json" \
    -d '{"WorkflowId":"nonexistent"}'
done
docker compose logs fleans-core 2>&1 | grep -c "POST" | head -1
```

**Expected:** Both replicas show request logs (round-robin distribution).

### 6. Teardown

```bash
docker compose down -v
```

**Expected:** All containers stopped and removed.

### 7. Dev path unchanged

```bash
cd src/Fleans && dotnet run --project Fleans.Aspire
```

**Expected:** Single silo starts normally via Aspire (no nginx, no replicas). Dashboard accessible.

## Checklist

- [ ] Docker compose stack generates from Aspire AppHost
- [ ] All services start and reach healthy state
- [ ] nginx proxies to fleans-core replicas
- [ ] API returns expected responses through nginx
- [ ] Both replicas receive traffic
- [ ] Dev path (`dotnet run --project Fleans.Aspire`) still works
