---
title: Deployment
description: Deploy Fleans to Docker Compose, Kubernetes (non-Helm), or a bare VM. Production-ready topology, configuration, and operations guidance.
sidebar:
  order: 6
---

Aspire is Fleans' development orchestrator — not a production runtime. To run Fleans in staging or production, you need different artifacts: a Docker Compose stack, Kubernetes manifests, or a set of services managed by your VM's process supervisor. This page walks through each path.

> **Helm on Kubernetes:** if you control Kubernetes, the Helm chart published to `https://nightbaker.github.io/fleans` is the recommended path. See [Self-host with Helm](/fleans/guides/self-host-helm/) for the install walkthrough. This page covers (1) the released Docker Compose bundle, (2) raw Kubernetes manifests extracted from the Helm chart via `helm template` for GitOps users who want YAML they can commit and edit.

## Topology overview

A production Fleans cluster has these components:

| Component | Image / process | Required | Purpose |
| --- | --- | --- | --- |
| **Core silo** (`fleans-api`) | `ghcr.io/nightbaker/fleans-api` with `Fleans__Role=Core` | yes | API endpoints, coordinator grains, workflow state. The same image hosts a `Combined` silo if `Fleans__Role` is unset or `Combined`. |
| **Worker silo** (`fleans-worker`) | `ghcr.io/nightbaker/fleans-worker` (defaults `Fleans__Role=Worker`) | optional | `[StatelessWorker]` script + condition grains. Skip if a Combined-role API silo handles them. |
| **Custom-task worker** (your-fork-of-`fleans-custom-worker`) | not published by Fleans — see [Custom Worker Host](/fleans/guides/custom-worker-host/) | optional | Plugin authors fork `Fleans.CustomWorkerHost` and ship their own image. Provides isolation for plugin failures and lets plugin code live outside the engine repo. |
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

The release pipeline attaches `docker-compose-v<TAG>.zip` to every GitHub Release. Download and unpack:

```bash
gh release download v<TAG> --repo nightBaker/fleans -p 'docker-compose-*.zip'
unzip docker-compose-v<TAG>.zip -d fleans-compose
cd fleans-compose
```

The bundle ships a self-contained `compose.yaml`, an `.env` file with generated secrets, and a `docker-compose.override.yaml` (empty by default — use it for managed-Postgres / managed-Redis overrides per [§ Override managed Postgres / Redis](#override-managed-postgres--redis) below).

Bring the stack up:

```bash
docker compose up -d
docker compose ps
```

Service URLs (default host ports from the post-processed bundle — confirm in `.env`):

- API: <http://localhost:8081>
- Admin UI: <http://localhost:8080>
- MCP server: internal-only by default
- Health: `curl http://localhost:8081/alive`

For the full lifecycle walkthrough (`.env` tuning, persistence backups, upgrade path, troubleshooting) see [Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/). The sub-sections below cover the topology overrides this reference page owns.

### Override managed Postgres / Redis

The bundled `compose.yaml` includes in-cluster `postgres` and `redis` services by default. To use managed services (RDS, ElastiCache, Cloud SQL, Memorystore, …), edit `docker-compose.override.yaml`:

1. Override `postgres` and `redis` (or `orleans-redis`) with `deploy: { replicas: 0 }` so they don't start (or drop them from `compose.yaml` if you prefer the bundle to be source-of-truth — leaving the override file as your only diff is the recommended pattern).
2. Drop the `depends_on` entries that reference them on `fleans-core` / `fleans-management` / `fleans-mcp`.
3. Replace the auto-generated connection strings with the managed endpoints:

```yaml
# docker-compose.override.yaml
services:
  fleans-core:
    environment:
      - ConnectionStrings__fleans=Host=db.example.internal;Database=fleans;Username=fleans;Password=${PG_PASSWORD}
      - ConnectionStrings__fleans-query=Host=db-replica.example.internal;Database=fleans;Username=fleans_ro;Password=${PG_RO_PASSWORD}
      - ConnectionStrings__orleans-redis=redis.example.internal:6379,password=${REDIS_PASSWORD}
      - Persistence__Provider=Postgres
  # repeat for fleans-management, fleans-mcp
```

The connection-string keys are the canonical names the silos expect: `fleans` (write), `fleans-query` (optional read replica — falls back to `fleans` when absent), and `orleans-redis` (clustering + grain storage). `ConnectionStrings__` (double underscore) is the standard .NET environment-variable form of `ConnectionStrings:fleans`.

### Scale workers

```bash
docker compose up -d --scale fleans-core=3
```

Stateless worker grains are placed across all available `Worker`/`Combined` silos automatically — Orleans handles routing through the Redis cluster table.

### Health checks

```bash
docker compose exec fleans-core curl -fsS http://localhost:8080/alive
docker compose exec fleans-core curl -fsS http://localhost:8080/health
```

### Logs and teardown

```bash
docker compose logs -f fleans-core
docker compose down               # keep volumes
docker compose down -v            # also drop data
```

## Path 2 — Kubernetes (raw manifests via `helm template`)

For GitOps repos or environments where Helm isn't on the cluster-side install path, extract raw manifests from the released chart using `helm template`. The output is plain YAML you can commit, diff, and edit by hand — but produced from a released artifact, so you stay on a maintained chart version.

### Generate manifests

```bash
helm repo add nightbaker https://nightbaker.github.io/fleans
helm repo update

helm template fleans nightbaker/fleans -n fleans \
  --set persistence.provider=Postgres \
  > out/k8s/manifests.yaml
```

The output contains a `Deployment` + `Service` for each Fleans workload (`fleans-core`, `fleans-management`, `fleans-mcp`), a `Secret` for the Postgres connection string, and `StatefulSet` resources for the in-cluster Postgres + Redis when the chart's `postgres.enabled` / `redis.enabled` defaults aren't overridden. Re-run `helm template` whenever you bump the chart version.

### Apply

```bash
kubectl create namespace fleans
kubectl apply -n fleans -f out/k8s/manifests.yaml
kubectl get -n fleans pods -w
```

Wait until every pod reaches `Running` with both containers `1/1`.

### Use managed Postgres and Redis

For any production cluster, point the silos at managed databases instead of in-cluster StatefulSets. Pass the override at `helm template` time:

```bash
helm template fleans nightbaker/fleans -n fleans \
  --set persistence.provider=Postgres \
  --set postgres.enabled=true \
  --set 'extraEnv[0].name=ConnectionStrings__fleans' \
  --set 'extraEnv[0].value=Host=db.example.internal;Database=fleans;Username=fleans;Password=...' \
  --set 'extraEnv[1].name=ConnectionStrings__orleans-redis' \
  --set 'extraEnv[1].value=redis.example.internal:6379,password=...' \
  > out/k8s/manifests.yaml
```

:::caution
The chart currently couples `persistence.provider=postgres` to the bundled-Postgres secret. Setting `postgres.enabled=false` while keeping `persistence.provider=Postgres` produces a pod stuck in `CreateContainerConfigError` referencing a non-existent secret. Until the chart fix lands ([#601](https://github.com/nightBaker/fleans/issues/601)), keep `postgres.enabled=true` even when supplying an external `ConnectionStrings__fleans` via `extraEnv` — the chart-managed Secret is harmlessly unused but the chart references it.
:::

### Probes and resources

The chart emits the canonical Fleans probes on every silo + host. Confirm in the generated manifests:

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
kubectl -n fleans scale deployment fleans-core --replicas=3
```

For external access to the admin UI, add an `Ingress` (or a `LoadBalancer` Service) targeting `fleans-management` on port 8080. The chart ships an [Ingress template](https://github.com/nightBaker/fleans/blob/main/charts/fleans/templates/ingress.yaml) you can re-extract via `helm template ... --set ingress.enabled=true`.

### When to prefer the chart directly

`helm install` is the lower-friction path when Helm IS on the cluster-side install path — `helm upgrade` gracefully handles config changes; `helm rollback` exists. Use `helm template` only when (a) you commit YAML to a GitOps repo, (b) you need to hand-edit manifests Helm doesn't expose as values, or (c) Helm isn't available cluster-side. See [Self-host with Helm](/fleans/guides/self-host-helm/) for the install walkthrough.

## Reverse proxy and TLS termination

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
    proxy_pass http://127.0.0.1:8081/;       # Fleans.Api
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }
}
```

When `Fleans.Web` runs behind a TLS-terminating proxy, set the `KnownProxies` / `KnownNetworks` allowlist (see [Authentication](/fleans/reference/authentication/)) so OIDC redirect URIs are built from the public host.

### Containerless bare-VM hosts

The engine ships only OCI container images and the Helm chart. To run on a bare VM without a container runtime, the supported path is to run the released images under `podman` or rootless `docker` driven by systemd — once the container is launched, the rest of this page applies. The engine does not publish standalone `.tar.gz` binaries.

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

- [Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/) — full Compose install walkthrough (download, run, `.env`, persistence, upgrade, troubleshooting).
- [Self-host with Helm](/fleans/guides/self-host-helm/) — Helm install walkthrough (verify chart + images, `values.yaml` reference, production checklist).
- [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) — Helm-specific quick start and chart values reference.
- [Persistence](/fleans/reference/persistence/) — SQLite vs PostgreSQL provider config and migration model.
- [Streaming](/fleans/reference/streaming/) — Memory vs Kafka provider config and at-least-once semantics.
- [Authentication](/fleans/reference/authentication/) — OIDC + JWT setup for API and admin UI, reverse-proxy notes.
