---
title: Deployment
description: Deploy Fleans to Docker Compose, Kubernetes (non-Helm), or a bare VM. Production-ready topology, configuration, and operations guidance.
sidebar:
  order: 6
---

Aspire is Fleans' development orchestrator — not a production runtime. To run Fleans in staging or production, you need different artifacts: a Docker Compose stack, Kubernetes manifests, or a set of services managed by your VM's process supervisor. This page walks through each path.

> **Helm on Kubernetes:** if you control Kubernetes, the Helm chart at [`charts/fleans/`](https://github.com/nightBaker/fleans/tree/main/charts/fleans) is the recommended path. See [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) for the Helm-specific quick start. This page covers the **non-Helm** Kubernetes story (raw `aspire publish -t kubernetes`) for users who want manifests they can edit, GitOps-commit, or extend, plus the Docker Compose and bare-VM stories the Helm chart does not cover.

## Topology overview

A production Fleans cluster has these components:

| Component | Image / process | Required | Purpose |
| --- | --- | --- | --- |
| **Core silo** (`fleans-api`) | `ghcr.io/nightbaker/fleans-api` with `Fleans__Role=Core` | yes | API endpoints, coordinator grains, workflow state. The same image hosts a `Combined` silo if `Fleans__Role` is unset or `Combined`. |
| **Worker silo** (`fleans-worker`) | `ghcr.io/nightbaker/fleans-worker` (defaults `Fleans__Role=Worker`) | optional | `[StatelessWorker]` script + condition grains. Skip if a Combined-role API silo handles them. |
| **Custom-task worker** (`fleans-custom-worker`) | `ghcr.io/nightbaker/fleans-custom-worker` | optional | Hosts custom-task plugin grains (REST caller, user plugins). Adds isolation for plugin failures. |
| **Web admin UI** (`fleans-web`) | `ghcr.io/nightbaker/fleans-web` | optional | Blazor Server admin UI on port `8080`. |
| **MCP server** (`fleans-mcp`) | `ghcr.io/nightbaker/fleans-mcp` | optional | MCP server on port `5200` for AI-agent integration. |
| **Redis** | `redis:7-alpine` or managed | yes | Orleans clustering, grain storage, key-protection keys. |
| **PostgreSQL** | `postgres:16` or managed | recommended for prod | Workflow event store + read model. SQLite is local-dev only. |
| **Kafka** | any Kafka broker | optional | Replaces in-memory streaming when at-least-once durability is required. |

:::caution[Streaming: do not point Kafka at managed services in v1]
The Kafka provider is plaintext-only — see [Streaming → Production-readiness gaps](/fleans/reference/streaming/#production-readiness-gaps) before pointing `Fleans:Streaming:Kafka:Brokers` at Confluent Cloud, MSK, Aiven, or Redpanda Cloud. The Memory provider has no production-readiness gaps.
:::

All silo and host processes expose two health endpoints, wired through `MapDefaultEndpoints()` in `Fleans.ServiceDefaults`:

- `/alive` — liveness only (process up). **Use for Kubernetes liveness probes.**
- `/health` — full check (Redis + Postgres reachable). **Use for readiness probes** so a transient dependency blip does not trigger pod restarts.

### Silo roles

`Fleans:Role` is read once at startup and stamped into the Orleans `SiloName` as `{role}-{machine}-{guid}`. Three values are accepted (case-insensitive):

| Value | Default for | Hosts |
| --- | --- | --- |
| `Core` | — | Coordinator grains (workflow instances, definitions, correlations). |
| `Worker` | `fleans-worker`, `fleans-custom-worker` | `[StatelessWorker]` script/condition grains and custom-task plugin grains. |
| `Combined` | `fleans-api` | Both. Single-binary topology for small deployments and dev. |

Invalid values throw at startup. The simplest production topology is one or more `Combined` silos plus Redis + Postgres; split into `Core` + `Worker` only when you need to scale or isolate them.

## Path 1 — Docker Compose

The Aspire AppHost ships a Compose-target publisher. From `src/Fleans/`:

```bash
cd src/Fleans
aspire publish --project Fleans.Aspire -t docker-compose -o out/compose
```

Output is a self-contained `out/compose/` folder containing `compose.yaml`, an `.env` file with generated secrets, and the build context for each service. Inspect what was generated before you bring it up:

```bash
ls out/compose
docker compose -f out/compose/compose.yaml config | head -100
```

Bring the stack up:

```bash
docker compose -f out/compose/compose.yaml up -d
docker compose -f out/compose/compose.yaml ps
```

Service URLs (default ports — confirm in the generated `compose.yaml`):

- API: <http://localhost:5000> (or whatever Aspire bound)
- Admin UI: <http://localhost:8080>
- Orleans Dashboard: hosted inside the silo on the configured port
- Health: `curl http://localhost:5000/alive`

### Override managed Postgres / Redis

The published Compose includes in-cluster `postgres` and `redis` services by default. To use managed services (RDS, ElastiCache, Cloud SQL, Memorystore, …):

1. Remove the `postgres` and `redis` (or `orleans-redis`) service blocks from `out/compose/compose.yaml`.
2. Drop the `depends_on` entries that reference them on `fleans-api` / `fleans-web` / `fleans-worker`.
3. Replace the auto-generated connection strings with the managed endpoints:

```yaml
services:
  fleans-api:
    environment:
      - ConnectionStrings__fleans=Host=db.example.internal;Database=fleans;Username=fleans;Password=${PG_PASSWORD}
      - ConnectionStrings__fleans-query=Host=db-replica.example.internal;Database=fleans;Username=fleans_ro;Password=${PG_RO_PASSWORD}
      - ConnectionStrings__orleans-redis=redis.example.internal:6379,password=${REDIS_PASSWORD}
      - Persistence__Provider=Postgres
```

The connection-string keys are the canonical names the silos expect: `fleans` (write), `fleans-query` (optional read replica — falls back to `fleans` when absent), and `orleans-redis` (clustering + grain storage). `ConnectionStrings__` (double underscore) is the standard .NET environment-variable form of `ConnectionStrings:fleans`.

### Scale workers

```bash
docker compose -f out/compose/compose.yaml up -d --scale fleans-worker=3
```

Stateless worker grains are placed across all available `Worker`/`Combined` silos automatically — Orleans handles routing through the Redis cluster table.

### Health checks

```bash
docker compose -f out/compose/compose.yaml exec fleans-api curl -fsS http://localhost:8080/alive
docker compose -f out/compose/compose.yaml exec fleans-api curl -fsS http://localhost:8080/health
```

### Logs and teardown

```bash
docker compose -f out/compose/compose.yaml logs -f fleans-api
docker compose -f out/compose/compose.yaml down               # keep volumes
docker compose -f out/compose/compose.yaml down -v            # also drop data
```

## Path 2 — Kubernetes (non-Helm, manifests via Aspire publish)

> **Preview notice.** `aspire publish -t kubernetes` is powered by `Aspire.Hosting.Kubernetes`, which is currently a **preview-only NuGet package** (`13.2.3-preview.1.26217.6` at the time of writing — pinned alongside the rest of the Aspire 13.2.3 stack). Its output schema can change between Aspire releases. The Helm chart at [`charts/fleans/`](https://github.com/nightBaker/fleans/tree/main/charts/fleans) is the **supported** production path — see [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/). Choose this section when you need raw manifests for GitOps, manual editing, or environments where Helm isn't an option.

### Generate manifests

```bash
cd src/Fleans
aspire publish --project Fleans.Aspire -t kubernetes -o out/k8s
```

Output is a folder of `.yaml` files: a `Deployment` + `Service` per Fleans process (`fleans-api`, `fleans-web`, `fleans-worker`, `fleans-custom-worker`, `fleans-mcp`), a `ConfigMap` per service, and `StatefulSet` resources for the in-cluster Postgres + Redis if you didn't disable them.

### Apply

```bash
kubectl create namespace fleans
kubectl apply -n fleans -f out/k8s/
kubectl get -n fleans pods -w
```

Wait until every pod reaches `Running` with both containers `1/1`.

### Use managed Postgres and Redis

For any production cluster, point the silos at managed databases instead of in-cluster StatefulSets:

1. **Delete the StatefulSets** (`postgres.yaml`, `redis.yaml`, or whatever Aspire emitted) and their PVC claims from `out/k8s/`.
2. **Create Secrets** with the connection strings:

```bash
kubectl create secret generic fleans-pg \
  --from-literal=connection-string='Host=db.example.internal;Database=fleans;Username=fleans;Password=...'
kubectl create secret generic fleans-redis \
  --from-literal=connection-string='redis.example.internal:6379,password=...'
```

3. **Patch the Deployments** to source the env vars from the Secrets:

```yaml
env:
  - name: ConnectionStrings__fleans
    valueFrom:
      secretKeyRef:
        name: fleans-pg
        key: connection-string
  - name: ConnectionStrings__orleans-redis
    valueFrom:
      secretKeyRef:
        name: fleans-redis
        key: connection-string
  - name: Persistence__Provider
    value: Postgres
```

### Probes and resources

Aspire emits placeholder probes; replace with the canonical Fleans probes on every silo + host:

```yaml
livenessProbe:
  httpGet: { path: /alive, port: 8080 }
  initialDelaySeconds: 15
  periodSeconds: 10
readinessProbe:
  httpGet: { path: /health, port: 8080 }
  initialDelaySeconds: 5
  periodSeconds: 5
```

**Why the split:** `/alive` is process-only — a Redis blip won't kill pods. `/health` is full-fabric — it pulls a degraded pod out of the Service until dependencies recover.

### Scale and ingress

```bash
kubectl -n fleans scale deployment fleans-worker --replicas=3
```

For external access to the admin UI, add an `Ingress` (or a `LoadBalancer` Service) targeting `fleans-web` on port 8080. The Helm chart's [Ingress template](https://github.com/nightBaker/fleans/blob/main/charts/fleans/templates/ingress.yaml) is a useful reference.

### Helm chart vs. raw manifests

| Question | Helm chart | Raw manifests |
| --- | --- | --- |
| Stable across Aspire releases? | Yes | No (preview pin) |
| GitOps-friendly diff? | Values file | Full manifest |
| Custom resource shapes? | Limited to chart's surface | Edit anything |
| Built-in OIDC / TLS templates? | Yes | DIY |
| Recommended? | **Yes** | When Helm isn't an option |

## Path 3 — Bare VM

For users who can't or won't run a container runtime, build the services as plain .NET 10 binaries and let `systemd` (Linux) or Windows Service Manager keep them up.

### Publish

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj         -c Release -o /opt/fleans/api
dotnet publish Fleans.Web/Fleans.Web.csproj         -c Release -o /opt/fleans/web
dotnet publish Fleans.WorkerHost/Fleans.WorkerHost.csproj -c Release -o /opt/fleans/worker
dotnet publish Fleans.Mcp/Fleans.Mcp.csproj         -c Release -o /opt/fleans/mcp
```

> **Aspire-free path.** This route uses raw `dotnet publish` only — no Aspire CLI required at deploy time. Per-service `dotnet publish /t:PublishContainer` is also Aspire-free if you want OCI images on the VM (`fleans-api:0.1.0` etc.) without orchestrator output. Only the Docker Compose and Kubernetes paths above need `aspire publish` to materialise multi-service manifests.

### systemd unit — Fleans.Api (Core silo)

Save as `/etc/systemd/system/fleans-api.service`:

```ini
[Unit]
Description=Fleans API (Orleans Core silo)
After=network-online.target redis.service postgresql.service
Wants=network-online.target

[Service]
Type=notify
User=fleans
Group=fleans
WorkingDirectory=/opt/fleans/api
ExecStart=/usr/bin/dotnet /opt/fleans/api/Fleans.Api.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=fleans-api

# .NET runtime
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
Environment=ASPNETCORE_URLS=http://0.0.0.0:8080

# Fleans config
Environment=Fleans__Role=Core
Environment=Persistence__Provider=Postgres
Environment=ConnectionStrings__fleans=Host=db.internal;Database=fleans;Username=fleans;Password=__PG_PWD__
Environment=ConnectionStrings__fleans-query=Host=db-replica.internal;Database=fleans;Username=fleans_ro;Password=__PG_RO_PWD__
Environment=ConnectionStrings__orleans-redis=redis.internal:6379,password=__REDIS_PWD__

# Optional: streaming
# Environment=Fleans__Streaming__Provider=Kafka
# Environment=Fleans__Streaming__Kafka__Brokers=kafka1.internal:9092,kafka2.internal:9092

# Optional: API auth (any OIDC provider)
# Environment=Authentication__Authority=https://idp.example.com/realms/fleans
# Environment=Authentication__Audience=fleans-api

[Install]
WantedBy=multi-user.target
```

Repeat for `fleans-web`, `fleans-worker`, and `fleans-mcp` (different `WorkingDirectory`, `ExecStart`, and `Description`; same env block; `Fleans__Role=Worker` for the worker; web/mcp don't read `Fleans__Role`). Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now fleans-api fleans-web fleans-worker fleans-mcp
sudo systemctl status fleans-api
journalctl -u fleans-api -f
```

### Reverse proxy (nginx)

Terminate TLS at nginx (or Caddy, HAProxy, Traefik) and forward to the admin UI / API:

```nginx
server {
  listen 443 ssl http2;
  server_name fleans.example.com;
  ssl_certificate     /etc/letsencrypt/live/fleans.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/fleans.example.com/privkey.pem;

  location / {
    proxy_pass http://127.0.0.1:8080;        # Fleans.Web admin UI
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;  # Blazor Server uses WebSockets
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  location /api/ {
    proxy_pass http://127.0.0.1:5000/;       # Fleans.Api
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }
}
```

When `Fleans.Web` runs behind a TLS-terminating proxy, set the `KnownProxies` / `KnownNetworks` allowlist (see [Authentication](/fleans/reference/authentication/)) so OIDC redirect URIs are built from the public host.

## Configuration reference

The full canonical list of every configuration key — what it does, where it's read in source, and which environment-variable equivalent operators set on a container or systemd unit — is on the dedicated **[Configuration](/fleans/reference/configuration/)** page. The summary tables below cover the deployment-relevant subset; cross-link to that page for the per-host applicability matrix and source pins.

`:` in config keys becomes `__` (double underscore) in environment variables — see [Configuration / The naming rule](/fleans/reference/configuration/#the-naming-rule).

### Silo role and identity

| Key | Allowed values | Default | Read by | Notes |
| --- | --- | --- | --- | --- |
| `Fleans:Role` | `Core`, `Worker`, `Combined` (case-insensitive) | `Combined` (Api), `Worker` (WorkerHost) | `Fleans.Api`, `Fleans.WorkerHost` | Stamped into `SiloName`. Invalid values throw at startup. |

### Connection strings

| Key | Required | Read by | Notes |
| --- | --- | --- | --- |
| `ConnectionStrings:fleans` | yes (Postgres mode) | All silos | Workflow command-side database (event store + write model). |
| `ConnectionStrings:fleans-query` | no | All silos | Read-replica for the query-side projection. Falls back to `fleans` when unset. |
| `ConnectionStrings:orleans-redis` | yes (multi-silo) | All silos | Orleans clustering, grain storage, PubSubStore. **Note the name — it's `orleans-redis`, not bare `redis`.** |

### Persistence

| Key | Allowed values | Default | Notes |
| --- | --- | --- | --- |
| `Persistence:Provider` | `Sqlite`, `Postgres` (case-insensitive) | `Sqlite` | `Postgres` runs `MigrateAsync()` at startup; `Sqlite` runs `EnsureCreated()`. SQLite is dev-only — see [Persistence](/fleans/reference/persistence/). |

### Streaming

| Key | Allowed values | Default | Notes |
| --- | --- | --- | --- |
| `Fleans:Streaming:Provider` | `Memory`, `Kafka` (case-insensitive) | `Memory` | See [Streaming](/fleans/reference/streaming/) for at-least-once semantics. |
| `Fleans:Streaming:Kafka:Brokers` | comma-separated `host:port` list | — | Required when `Provider=Kafka`. |
| `Fleans:Streaming:Kafka:ConsumerGroup` | string | `fleans` | |
| `Fleans:Streaming:Kafka:TopicPrefix` | string | `fleans-` | |

### Authentication

Both the API and the admin UI ship with **opt-in** authentication — when nothing is configured, both run unauthenticated, identical to dev mode. See [Authentication](/fleans/reference/authentication/) for the full key reference, role plans, and reverse-proxy guidance.

| Key | Required | Default | Notes |
| --- | --- | --- | --- |
| `Authentication:Authority` | to enable | — (auth disabled) | OIDC issuer. Setting this enables JWT enforcement on `/Workflow/*` (API) and OIDC sign-in on the admin UI. |
| `Authentication:Audience` | API | — | JWT `aud` claim the API requires. |
| `Authentication:RequireHttpsMetadata` | no | `true` | Set `false` only for local dev against an HTTP IdP. |
| `Authentication:ClientId` | Web | — | OIDC client ID for the Blazor Server admin UI. |
| `Authentication:ClientSecret` | Web | — | Client secret. Source from a Secret/Key Vault, not appsettings, in production. |

## Health checks and observability

- `/alive` and `/health` are exposed on every silo + host (`MapDefaultEndpoints`).
- The Orleans Dashboard is available on each silo at the configured port — port-forward or expose via your reverse proxy / Ingress for ad-hoc cluster inspection.
- `Fleans.ServiceDefaults` wires OpenTelemetry traces + metrics by default; point `OTEL_EXPORTER_OTLP_ENDPOINT` at your collector.
- A dedicated observability page (Prometheus scrape config, Grafana dashboards) is tracked in the project backlog.

## Update strategy

Fleans silos form a single Orleans cluster — rolling restarts are the standard upgrade path, but a few Orleans-specific invariants apply.

### Cluster-ID continuity (Orleans)

Orleans groups silos into a cluster by **`ClusterId`**. The default `ClusterId` is `dev` — fine for a single deployment, but you should set it explicitly per environment (`production`, `staging-eu`, …) so two unrelated deployments hitting the same Redis can't see each other's silos.

```bash
Orleans__ClusterId=fleans-production
Orleans__ServiceId=fleans
```

**Critical for rolling upgrades:** keep `ClusterId` (and the Redis clustering connection string) **identical across new and old pods/processes** during a rollout. The new silos use the Redis clustering table to discover and gossip with the old silos so grain handoff is graceful. If `ClusterId` changes, the new silos form a separate logical cluster, the old grains never re-balance, and in-flight workflow events can be lost.

### Rolling restart

- Kubernetes: `kubectl rollout restart deployment/fleans-api -n fleans` — set `strategy.type=RollingUpdate`, `maxUnavailable=1`, `maxSurge=1` so at least one Core silo always serves the cluster table.
- Compose: `docker compose -f out/compose/compose.yaml up -d --no-deps --scale fleans-api=2 fleans-api` (start one extra), then drain old containers one at a time.
- systemd: `systemctl restart fleans-api` on each VM, one at a time, watching `journalctl -u fleans-api -f` until the new instance reports `Active silo count: N`.

### Blue / green and canary

For blue / green: stand up a second cluster with a **different** `ClusterId` (e.g. `fleans-production-green`) and its **own** Redis + Postgres, then flip the load balancer. This avoids cross-cluster grain confusion.

For canary on a single cluster: deploy the new image to a small subset of replicas (`maxSurge=1`, `maxUnavailable=0`), watch error rate / latency, and either continue the rollout or `kubectl rollout undo`.

### Schema migrations

PostgreSQL: migrations apply automatically on `Fleans.Api` startup via `MigrateAsync()`. Roll out a new image only after confirming the new migration is backwards-compatible (additive columns, non-breaking changes) — Fleans does not gate writes during migrations.

## See also

- [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) — Helm chart path for K8s.
- [Persistence](/fleans/reference/persistence/) — SQLite vs PostgreSQL provider config and migration model.
- [Streaming](/fleans/reference/streaming/) — Memory vs Kafka provider config and at-least-once semantics.
- [Authentication](/fleans/reference/authentication/) — OIDC + JWT setup for API and admin UI, reverse-proxy notes.
