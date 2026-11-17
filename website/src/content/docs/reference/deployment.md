---
title: Deployment
description: The components, roles and network ports of a Fleans cluster, and how to run it from the Compose bundle, as raw Kubernetes manifests, or as containers you start yourself — plus reverse proxies and restarts.
sidebar:
  order: 5
---

Fleans ships as container images on `ghcr.io/nightbaker/*`, a Docker Compose bundle and a Helm
chart, all attached to each [GitHub Release](https://github.com/nightBaker/fleans/releases). This
page describes what a cluster is made of and covers three ways to run it: (1) the released Compose
bundle, (2) raw Kubernetes manifests rendered from the released chart with `helm template`, and
(3) the containers started by your own tooling — ECS, Nomad, VMs or plain Docker. For a Helm
install, follow [Self-host with Helm](/fleans/guides/self-host-helm/).

## Topology overview

A production Fleans cluster has these components:

<figure class="arch-diagram" style="--arch-ratio: 1400 / 598">
  <iframe data-arch-src="/fleans/diagrams/fleans-cluster.html" src="/fleans/diagrams/fleans-cluster.html?embed=1" title="Fleans deployment architecture" loading="lazy"></iframe>
  <figcaption>The components of a production Fleans cluster and how they connect. <a href="/fleans/diagrams/fleans-cluster.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

| Component | Image / process | Required | Purpose |
| --- | --- | --- | --- |
| **Core silo** | `ghcr.io/nightbaker/fleans-api` with `Fleans__Role=Core` | yes | REST API, workflow instances, message and signal correlation, timers. With `Fleans__Role` unset or `Combined`, the same image runs everything in one silo. |
| **REST API** | `ghcr.io/nightbaker/fleans-api` with `Fleans__Role=Api` | optional | REST and the gRPC external-task endpoint only: an Orleans client with no grains, scaled on requests per second apart from the silos. Without it the Core / Combined silo serves REST. |
| **Worker silo** | `ghcr.io/nightbaker/fleans-worker` (defaults to `Fleans__Role=Worker`), or `fleans-api` with `Fleans__Role=Worker` | optional | Script tasks and the read-model projector (instance lists, finished-instance views). Without any Worker or Combined silo they run on the Core silos. |
| **Custom-task worker** | `ghcr.io/nightbaker/fleans-plugins`, or your own image from the [Hosting Workers](/fleans/guides/custom-worker-host/) template | optional | Custom tasks (REST Caller, Camunda external tasks, or yours). Connects to the API over gRPC with `Fleans__ExternalTaskWorker__EngineUrl`; joins no cluster, so plugin failures stay away from the engine. |
| **Web admin UI** | `ghcr.io/nightbaker/fleans-web` | optional | Blazor Server admin UI on port `8080`. An Orleans client. |
| **MCP server** | `ghcr.io/nightbaker/fleans-mcp` | optional | MCP server for AI-agent integration on port `8080` (the Helm chart moves it to `5200`). An Orleans client. |
| **Redis** | `redis` or managed | while a provider uses it | Orleans clustering, reminders and streams when their provider is Redis (the defaults), and the admin UI's Data Protection keys. Not needed once clustering and reminders are on Postgres and streams on Kafka / Azure Queue — see [Configuration → Redis](/fleans/reference/configuration/#redis). |
| **PostgreSQL** | `postgres` or managed | yes in production | Workflow journal, read model, and optionally membership and timers. SQLite is local-dev only. |
| **Kafka** or **Azure Queue Storage** | your broker / storage account | optional | Stream provider in place of Redis. See [Streaming](/fleans/reference/streaming/). |

The names differ per packaging: the Compose bundle calls the Core silo `fleans-core` and the admin
UI `fleans-management`; the Helm chart names its Deployments `core-<release>`,
`worker-<release>`, `<release>-api`, `<release>-web`, `<release>-mcp` and `<release>-plugins`.

:::note[Streaming: Kafka brokers with SASL or TLS]
Managed and secured Kafka clusters (Confluent Cloud, Aiven, Redpanda Cloud, MSK's SASL/SCRAM or TLS listeners) need the security keys in [Streaming → Kafka security](/fleans/reference/streaming/#kafka-security-sasl--tls); check [Production-readiness gaps](/fleans/reference/streaming/#production-readiness-gaps) for the remaining trade-offs.
:::

Every Fleans process serves two anonymous health endpoints on its HTTP port:

- `/alive` — liveness only (process up). **Use for liveness probes.**
- `/health` — readiness: the workflow database and the journal database are reachable, and Redis
  when the process uses it. **Use for readiness probes** so a dependency blip does not restart pods.

### Silo roles

`Fleans:Role` is read once at startup and stamped into the Orleans silo name as
`{role}-{machine}-{guid}` — with Kubernetes hosting on, the silo name is the pod name, which must
start with `{role}-`. Three silo values are accepted (case-insensitive), plus `Api` on `fleans-api`:

| Value | Default for | Hosts |
| --- | --- | --- |
| `Core` | — | Workflow instances, definitions, message and signal correlation, timers. |
| `Worker` | `fleans-worker` | Script tasks and the read-model projector. |
| `Combined` | `fleans-api` | Both. Single-silo topology for small deployments and dev. |
| `Api` | — | Nothing: `fleans-api` runs as an Orleans client, not a silo, and serves only the REST API and the gRPC external-task endpoint. Needs at least one `Core` or `Combined` silo in the cluster. |

Invalid values throw at startup. The simplest production topology is one `Combined` silo on a single PostgreSQL — see [Simplest start: one PostgreSQL](/fleans/guides/topology-single-postgres/); split into `Api` + `Core` + `Worker` when you need to scale or isolate them — see [Recommended for scale](/fleans/guides/topology-scalable/).

## Path 1 — Docker Compose

The release pipeline attaches `docker-compose-v<version>.zip` to every GitHub Release. Download and unpack:

```bash
gh release download v0.14.0 --repo nightBaker/fleans -p 'docker-compose-*.zip'
unzip docker-compose-v0.14.0.zip -d fleans
cd fleans
docker compose up -d
docker compose ps
```

The bundle holds two files: `docker-compose.yaml` and an `.env` with the image tags and generated
passwords. The host ports are fixed in `docker-compose.yaml`:

- Admin UI: <http://localhost:8080>
- REST API: <http://localhost:8081>
- MCP server: not published on the host
- Health, from the host: `curl -fsS http://localhost:8081/health`

[Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/) walks through the `.env`
variables, the telemetry dashboard, backups and troubleshooting.

### Override managed Postgres / Redis

Only image tags, ports, passwords, the cluster id and the OIDC settings come from `.env`. Every
other setting is a literal in `docker-compose.yaml`; change it in a `docker-compose.override.yaml`
you create next to it, which `docker compose` merges automatically:

```yaml
# docker-compose.override.yaml
x-managed: &managed
  ConnectionStrings__fleans: "Host=db.example.internal;Database=fleans;Username=fleans;Password=${PG_PASSWORD};Maximum Pool Size=20"
  ConnectionStrings__fleans-query: "Host=db-replica.example.internal;Database=fleans;Username=fleans_ro;Password=${PG_RO_PASSWORD}"
  # The bundle sets the Redis connection under both names; override both.
  ConnectionStrings__orleans-redis: "redis.example.internal:6379,password=${REDIS_PASSWORD}"
  ConnectionStrings__orleans_redis: "redis.example.internal:6379,password=${REDIS_PASSWORD}"

services:
  fleans-core:
    environment: *managed
  fleans-worker:
    environment: *managed
  fleans-management:
    environment: *managed
  fleans-mcp:
    environment: *managed
```

Add `PG_PASSWORD`, `PG_RO_PASSWORD` and `REDIS_PASSWORD` to `.env`. The bundled `postgres` and
`orleans-redis` containers then run unused; to drop them, delete those services and the
`depends_on` entries that name them from `docker-compose.yaml`.

The connection-string keys are the names the processes expect: `fleans` (write), `fleans-query`
(optional read replica — falls back to `fleans`), `fleans-journal` (optional database for the
workflow journal — falls back to `fleans`; see [Separate journal database](/fleans/reference/persistence/#separate-journal-database)),
and `orleans-redis` (clustering, reminders and streams while they use Redis).

### Scale Worker silos

```bash
docker compose up -d --scale fleans-worker=3
```

`fleans-worker` publishes no host port, so its replicas can run side by side. `fleans-core` binds
host port `8081` and cannot be scaled this way.

### Logs and teardown

```bash
docker compose logs -f fleans-core
docker compose down               # keep volumes
docker compose down -v            # also drop data
```

## Path 2 — Kubernetes (raw manifests via `helm template`)

For GitOps repos, or clusters where Helm is not on the install path, render plain manifests from
the released chart. The output is YAML you can commit, diff and edit — produced from a released
artifact, so you stay on a maintained chart version.

### Generate manifests

The chart is a GitHub Release asset; there is no Helm repository to add.

```bash
gh release download v0.14.0 --repo nightBaker/fleans -p 'fleans-0.14.0.tgz'
mkdir -p out/k8s
helm template fleans ./fleans-0.14.0.tgz -n fleans -f values-prod.yaml > out/k8s/manifests.yaml
```

With the defaults the output holds the `core-fleans` silo Deployment, the `fleans-web` and
`fleans-mcp` Deployments, a `Service` for each of them (`fleans-core`, `fleans-web`,
`fleans-mcp`), the `fleans-postgres` and `fleans-redis` StatefulSets with their Services, a
generated Postgres password `Secret`, and the `fleans-silo` ServiceAccount, Role and RoleBinding
for Kubernetes hosting. [What the chart deploys](/fleans/guides/self-host-helm/#what-the-chart-deploys)
lists every object and the values that turn it on. Render again for every new chart version.

:::caution
`helm template` cannot look up an existing `Secret`, so every render generates a new random
Postgres password. Set `postgres.password` or `postgres.existingSecret` in your values, or the
password changes each time you apply a new render.
:::

### Apply

```bash
kubectl create namespace fleans
kubectl apply -n fleans -f out/k8s/manifests.yaml
kubectl get -n fleans pods -w
```

Wait until every pod is `1/1 Running`.

### Use managed Postgres and Redis

Turn off the bundled servers and pass the connection strings through `extraEnv`, which the chart
adds to the `core`, `worker`, `api`, `web` and `mcp` pods:

```yaml
# values-prod.yaml
persistence:
  provider: Postgres
postgres:
  enabled: false
redis:
  enabled: false        # only once no provider uses the bundled Redis — see below
extraEnv:
  - name: ConnectionStrings__fleans
    valueFrom: { secretKeyRef: { name: fleans-pg, key: connection-string } }
  - name: ConnectionStrings__orleans-redis
    valueFrom: { secretKeyRef: { name: fleans-redis, key: connection-string } }
```

With `postgres.enabled=false` the chart references no Postgres `Secret` of its own. With
`redis.enabled=false` it sets no `ConnectionStrings__orleans-redis`, so supply your managed Redis
here while clustering, reminders or streams use Redis (the defaults), or move all three off Redis
as in [Running without Redis](/fleans/guides/self-host-helm/#running-without-redis).

### Probes and resources

The chart sets these probes on every Fleans pod (the plugins pod uses `/health` for both):

```yaml
livenessProbe:
  httpGet: { path: /alive, port: http }
  initialDelaySeconds: 30
  periodSeconds: 30
readinessProbe:
  httpGet: { path: /health, port: http }
  initialDelaySeconds: 10
  periodSeconds: 10
```

**Why the split:** `/alive` is process-only — a database or Redis blip does not restart pods.
`/health` takes a pod out of its Service until its dependencies recover.

### Scale and ingress

```bash
kubectl -n fleans scale deployment core-fleans --replicas=3
```

With an autoscaler enabled (`core.autoscaling.enabled`) the HPA owns the replica count instead.
The chart's optional Ingress (`--set ingress.enabled=true`) routes only to the `fleans-web`
Service on port 8080. To expose the REST API, add your own Ingress rule to `fleans-core` (or
`fleans-api` with `api.enabled=true`) on port 8080.

### When to prefer the chart directly

`helm install` is the lower-friction path when Helm is on the cluster-side install path — `helm upgrade` handles config changes and `helm rollback` exists. Use `helm template` only when (a) you commit YAML to a GitOps repo, (b) you need to hand-edit manifests Helm doesn't expose as values, or (c) Helm isn't available cluster-side. See [Self-host with Helm](/fleans/guides/self-host-helm/).

## Running the containers yourself

Without the bundle or the chart — on ECS, Nomad, VMs or plain Docker — you start each process
yourself. Fleans publishes no standalone binaries; on a VM, run the images under Docker or Podman,
for example from a systemd unit.

### Processes and ports

| Process | Image · `Fleans__Role` | HTTP port (container) | Orleans ports | Opens connections to |
| --- | --- | --- | --- | --- |
| Core silo | `fleans-api` · `Core` (unset = `Combined`) | 8080: REST, gRPC external tasks, `/health` | silo `11111`, gateway `30000` | other silos' silo port; PostgreSQL; Redis / Kafka |
| Worker silo | `fleans-worker` (role defaults to `Worker`) | 8080: `/health`, `/alive` only | silo `11111`, gateway `30000` | other silos' silo port; PostgreSQL; Redis / Kafka |
| REST-only API | `fleans-api` · `Api` | 8080: REST, gRPC external tasks | none (Orleans client) | silos' gateway port; PostgreSQL; Redis while clustering is on Redis |
| Admin UI | `fleans-web` | 8080 | none (Orleans client) | silos' gateway port; PostgreSQL; Redis while clustering is on Redis; your OIDC provider |
| MCP server | `fleans-mcp` | 8080 | none (Orleans client) | silos' gateway port; PostgreSQL; Redis while clustering is on Redis |
| Custom-task worker | `fleans-plugins` or your image | its own | none | the REST host's HTTP port (gRPC) |

What to open between hosts:

- **Silo port** (`Orleans__Endpoints__SiloPort`, default `11111`): between every pair of silos,
  both directions.
- **Gateway port** (`Orleans__Endpoints__GatewayPort`, default `30000`): on every silo — Worker
  silos included — from every Orleans client (REST-only API, admin UI, MCP).
- **HTTP 8080**: on the REST hosts (Core / Combined / Api) from your services, load balancer and
  custom-task workers; on the admin UI from operators.
- **PostgreSQL, Redis, Kafka**: from every Fleans process except the custom-task workers, which need
  only their own databases. Every process applies the schema at startup, so its database user must
  be allowed to create tables.

Each silo writes its address and ports into the membership table, and the others dial exactly that
address. A container on a Docker bridge network advertises its container IP, which other hosts
cannot reach, so on more than one host run the silos with host networking (`--network host`). On
a host with several interfaces, set `Orleans__Endpoints__AdvertisedIPAddress` to the address the
other hosts reach. The HTTP port is `8080` in every image; change it with `HTTP_PORTS`.

### Minimum settings

Every Fleans process of one cluster gets the same values:

```bash
Orleans__ClusterId=fleans-prod
Orleans__ServiceId=fleans
Persistence__Provider=Postgres     # unset = a SQLite file inside the container
ConnectionStrings__fleans=Host=10.0.0.10;Database=fleans;Username=fleans;Password=change-me;Maximum Pool Size=20
# While clustering, reminders or streams use Redis (all three default to Redis):
ConnectionStrings__orleans-redis=10.0.0.11:6379,password=change-me
```

Per process, add:

- **Silos:** `Fleans__Role` (`Core`, `Worker` or `Combined`). Optionally the Orleans ports above.
- **REST-only API:** `Fleans__Role=Api`.
- **Admin UI, MCP:** nothing else to run; `Authentication__*` to turn sign-in on — see
  [Authentication](/fleans/reference/authentication/).
- **Custom-task worker:** `Fleans__ExternalTaskWorker__EngineUrl=http://<rest-host>:8080`
  instead of the settings above.

Leave out `Fleans__Kubernetes__Enabled` (it needs the Kubernetes API) and the
`Orleans__Clustering__*` keys the Compose bundle sets for its Redis: Fleans picks the membership
table from `Fleans__Clustering__Provider` (default `Redis`), and setting both fails at startup
when the provider is `Postgres`. Everything else is optional —
[Configuration](/fleans/reference/configuration/) lists every key.

### Example: three hosts with Docker

PostgreSQL at `10.0.0.10` and Redis at `10.0.0.11` already run. Put the settings above in
`fleans.env` on each host, then:

```bash
# Host A (10.0.0.21): Core silo — REST API on :8080, silo :11111, gateway :30000
docker run -d --name fleans-core --restart on-failure --network host \
  --stop-timeout 60 -e shutdownTimeoutSeconds=50 \
  --env-file fleans.env -e Fleans__Role=Core \
  ghcr.io/nightbaker/fleans-api:0.14.0

# Host B (10.0.0.22): Worker silo — silo :11111, gateway :30000
docker run -d --name fleans-worker --restart on-failure --network host \
  --stop-timeout 60 -e shutdownTimeoutSeconds=50 \
  --env-file fleans.env \
  ghcr.io/nightbaker/fleans-worker:0.14.0

# Host C (10.0.0.23): admin UI on :8080
docker run -d --name fleans-web --restart on-failure --network host \
  --env-file fleans.env \
  ghcr.io/nightbaker/fleans-web:0.14.0
```

Firewall: `11111` between A and B; `30000` on A and B from C; `8080` on A from your clients and on
C from operators; `5432` and `6379` from A, B and C. Check with `curl -fsS http://10.0.0.21:8080/health`.
`--stop-timeout 60` and `shutdownTimeoutSeconds=50` give a silo time to leave the cluster and hand
over its instances when it is stopped — see [High availability](/fleans/reference/high-availability/).
Add hosts by repeating the Core or Worker command; put a load balancer in front of the REST hosts.

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

With sign-in on, the admin UI honours `X-Forwarded-Proto` / `X-Forwarded-For` only from the
proxies you list — `Authentication__KnownProxies__0=10.0.0.5`, or a range with
`Authentication__KnownNetworks__0=10.0.0.0/24` (see
[Configuration → Authentication](/fleans/reference/configuration/#authentication)). Without it the
UI builds an `http://` OIDC redirect URI and the IdP rejects it. The Compose bundle sets
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` instead, which makes ASP.NET Core accept the headers
from any address: there, let only your proxy reach the published ports.

## Configuration

Every key, its environment-variable form and the hosts that read it are on
[Configuration](/fleans/reference/configuration/). `:` in a key becomes `__` (double underscore)
in an environment variable — see [The naming rule](/fleans/reference/configuration/#the-naming-rule).
The ones a deployment always touches are the [minimum settings](#minimum-settings) above, the
[stream provider](/fleans/reference/configuration/#streaming) and
[authentication](/fleans/reference/configuration/#authentication).

## Health checks and observability

- `/alive` and `/health` are served on every Fleans process's HTTP port.
- The Orleans Dashboard is served only by the admin UI, at `/dashboard`, and only once the admin
  UI has OIDC sign-in configured (`Authentication__Authority` and `Authentication__ClientId`); it
  then requires a signed-in user. With auth off it is not served at all outside the `Development`
  environment.
- Every process exports traces, metrics and logs over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is
  set. See [Observability](/fleans/reference/observability/) for the metrics, health checks and
  dashboards, and [Configuring observability](/fleans/guides/configuring-observability/) for wiring.

## Update strategy

Fleans silos form one Orleans cluster. Restart them one at a time within a release; move to a new
release on fresh stores.

### Cluster-ID continuity (Orleans)

Orleans groups silos into a cluster by **`ClusterId`**. Set it explicitly per environment
(`production`, `staging-eu`, …) so two unrelated deployments that share a Redis or a membership
database cannot see each other's silos:

```bash
Orleans__ClusterId=fleans-production
Orleans__ServiceId=fleans
```

**Critical for rolling restarts:** keep `ClusterId`, `ServiceId` and the membership store (the
Redis or Postgres clustering connection) **identical across new and old pods/processes**. New silos
use the membership table to find the running ones, so instances move over gracefully. If
`ClusterId` changes, the new silos form a separate cluster next to the old one.

### Rolling restart

Within one release — configuration changes, scaling, node maintenance. What a restart does to
running workflows, how long failover takes, and what clients must retry:
[High availability](/fleans/reference/high-availability/#rolling-restarts).

- Kubernetes: `kubectl rollout restart deployment/core-fleans -n fleans` (and `worker-fleans`).
  The chart's Deployments use Kubernetes' default rolling update, which replaces a three-replica
  Deployment one pod at a time.
- Compose: `docker compose up -d` recreates only the services whose settings changed;
  `docker compose restart fleans-worker` restarts one service.
- Your own containers: stop and start one silo at a time with a stop timeout of 60 s
  (`docker stop -t 60`), and wait for its `/health` before the next.

### New releases

Until 1.0.0 a new release starts on fresh, empty stores: schemas, journal events and stream
payloads change in place, and nothing converts the old data. Rolling the new image over an existing
database is not supported. Stand up the new release next to the old one — a **different**
`ClusterId` and its **own** Redis / Kafka and databases — move new traffic to it, and remove the
old cluster once it has drained. Or stop, empty the stores and start again. Both are described in
[Upgrading](/fleans/guides/upgrading/).

## See also

- [Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/) — full Compose install walkthrough (download, run, `.env`, persistence, troubleshooting).
- [Self-host with Helm](/fleans/guides/self-host-helm/) — Helm install walkthrough (verify chart + images, what the chart deploys, `values.yaml` reference, production checklist).
- [Upgrading](/fleans/guides/upgrading/) — moving to a new release before 1.0.
- [Persistence](/fleans/reference/persistence/) — SQLite vs PostgreSQL provider config and migration model.
- [Streaming](/fleans/reference/streaming/) — stream providers and at-least-once semantics.
- [Authentication](/fleans/reference/authentication/) — OIDC + JWT setup for API and admin UI, reverse-proxy notes.
