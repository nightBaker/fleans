---
title: Self-host with the Helm chart
description: Install Fleans on Kubernetes from a release-asset Helm chart tarball. Walks through download, install, what the chart deploys, values reference, production checklist, security defaults, new releases, and troubleshooting.
---

The Fleans release pipeline publishes a packaged Helm chart (`fleans-<VER>.tgz`)
on every `v<SemVer>` tag. This guide walks through the install lifecycle
against a real Kubernetes cluster: acquire the tarball, install with sane
defaults, harden for production, move to a new release, and recover from
common failures. The chart is published only as this GitHub Release asset:
there is no Helm repository to `helm repo add`, and no OCI registry yet. To
render plain manifests for GitOps instead, see
[Deployment → Kubernetes](/fleans/reference/deployment/#path-2--kubernetes-raw-manifests-via-helm-template).

## Prerequisites

- **Kubernetes 1.27+** recommended. The chart declares no minimum
  `kubeVersion`; its CI lints and renders it with `helm template` but does
  not install it on a cluster.
- **Helm 3.12+**. Older 3.x may work but is unsupported.
- **A `StorageClass`** if you keep the chart-managed Postgres
  StatefulSet (`postgres.persistentVolume.enabled=true`, the default).
  Skip when pointing at an external Postgres.
- **Pull access** to `ghcr.io/nightbaker/fleans-{api,web,mcp}` — public for
  the Fleans repo's published images. Provide `imagePullSecrets` if you
  mirror to a private registry.
- A namespace you control (`kubectl create ns fleans` or use any existing).

## 1. Download the chart

```bash
gh release download v0.14.0 --repo nightBaker/fleans -p 'fleans-*.tgz'
```

Or via curl:

```bash
curl -LO https://github.com/nightBaker/fleans/releases/download/v0.14.0/fleans-0.14.0.tgz
```

:::note[OCI publishing is deferred]
Publishing the chart to an OCI registry (`oci://ghcr.io/nightbaker/charts/fleans`)
is tracked in [#410](https://github.com/nightBaker/fleans/issues/410). Until
then, the GitHub-Release-asset path is the supported install vector.
:::

## Verify the chart and images

The release pipeline signs both the chart tarball (as a blob) and every container image (by manifest digest). Run two checks before `helm install`:

**1. Verify the helm chart tarball.** Download `fleans-0.14.0.tgz`, `fleans-0.14.0.tgz.sig`, and `fleans-0.14.0.tgz.crt` from the same GitHub Release, then:

```bash
cosign verify-blob \
  --certificate fleans-0.14.0.tgz.crt \
  --signature   fleans-0.14.0.tgz.sig \
  --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  fleans-0.14.0.tgz
```

**2. Verify each container image.** The chart pulls `fleans-api` (for the `core`, `worker` and `api` Deployments), `fleans-web` and `fleans-mcp`, plus `ghcr.io/nightbaker/fleans-plugins` when `plugins.enabled=true` (released from the separate [fleans-plugins](https://github.com/nightBaker/fleans-plugins) repository). The loop also checks `fleans-worker`, which the chart does not use but the Compose bundle does:

```bash
for SVC in api web worker mcp; do
  cosign verify \
    --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
    --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    ghcr.io/nightbaker/fleans-$SVC:0.14.0
done
```

For production Kubernetes installs, the recommended enforcement is the Sigstore Policy Controller (`policy.sigstore.dev/v1beta1 ClusterImagePolicy`) or Kyverno's `verifyImages`/`verifyManifests` rules.

## 2. Quick install

```bash
helm install fleans ./fleans-0.14.0.tgz \
  --namespace fleans \
  --create-namespace

kubectl wait --for=condition=ready pod \
  -l app.kubernetes.io/instance=fleans \
  --timeout=180s --namespace fleans

kubectl port-forward -n fleans svc/fleans-web 8080:8080
```

Open [http://localhost:8080](http://localhost:8080) — the admin UI loads.
Tear down at any time with `helm uninstall fleans -n fleans`.

### What the chart deploys

Rendered with `helm template`. `<fullname>` is the release name, with `-fleans` appended unless
the release name already contains `fleans` — for `helm install fleans …` it is `fleans`, so the
objects are `core-fleans`, `fleans-core`, `fleans-web` and so on.

| Object | Name | Image · port | Default |
| --- | --- | --- | --- |
| Core silos — Deployment | `core-<fullname>` | `fleans-api` · 8080, `Fleans__Role=Core` | on |
| Core REST — Service | `<fullname>-core` | 8080 | on |
| Worker silos — Deployment (no Service) | `worker-<fullname>` | `fleans-api` · 8080, `Fleans__Role=Worker` | `worker.enabled=false` |
| REST-only API — Deployment + Service | `<fullname>-api` | `fleans-api` · 8080, `Fleans__Role=Api` | `api.enabled=false` |
| Admin UI — Deployment + Service | `<fullname>-web` | `fleans-web` · 8080 | `web.enabled=true` |
| MCP server — Deployment + Service | `<fullname>-mcp` | `fleans-mcp` · 5200 | `mcp.enabled=true` |
| Custom-task worker — Deployment (no Service) | `<fullname>-plugins` | `fleans-plugins` · 8080 | `plugins.enabled=false` |
| PostgreSQL — StatefulSet + Service + password Secret | `<fullname>-postgres` | `postgres:16` · 5432 | `postgres.enabled=true` |
| Redis — StatefulSet + Service | `<fullname>-redis` | `redis:7-alpine` · 6379 | `redis.enabled=true` |
| Silo ServiceAccount + Role + RoleBinding | `<fullname>-silo` | — | `clustering.kubernetes.enabled=true` |
| Ingress (to the admin UI only) | `<fullname>` | — | `ingress.enabled=false` |
| HorizontalPodAutoscaler / KEDA `ScaledObject` | per silo Deployment | — | `*.autoscaling.enabled=false` |

With the defaults there are no Worker silos: the core silo runs script tasks and the
read-model projector itself. Every Fleans pod has a liveness probe on `/alive`
(30 s initial delay, every 30 s) and a readiness probe on `/health` (10 s, every 10 s);
the plugins pod uses `/health` for both. Select all pods of the release with
`-l app.kubernetes.io/instance=<release>`, one workload with
`app.kubernetes.io/component=core|worker|api|web|mcp|plugins`.

## 3. `values.yaml` reference

The defaults below are extracted from `charts/fleans/values.yaml`. Override
on the install command line via `--set` or pass a `-f my-values.yaml` overlay.

| Key | Default | Description |
| --- | --- | --- |
| `image.api.repository` | `ghcr.io/nightbaker/fleans-api` | Image used by the `core`, `worker` and `api` Deployments. |
| `image.web.repository` | `ghcr.io/nightbaker/fleans-web` | Blazor Server admin UI image. |
| `image.mcp.repository` | `ghcr.io/nightbaker/fleans-mcp` | MCP server image. |
| `image.tag` | *(empty — falls back to chart `appVersion`)* | Pin to a specific tag for reproducible installs. |
| `image.pullPolicy` | `IfNotPresent` | Standard Kubernetes pull policy. |
| `imagePullSecrets` | `[]` | Add `[{name: my-pull-secret}]` when mirroring images. |
| `core.replicas` | `1` | Number of Core silos. |
| `core.resources.requests` | `cpu: 250m, memory: 512Mi` | Right-sized for ~10 active workflows; bump for higher throughput. |
| `core.resources.limits.memory` | `1Gi` | Hard cap. |
| `api.enabled` | `false` | Set `true` to add `<fullname>-api`: REST only (`Fleans__Role=Api`), an Orleans client with no grains, behind its own Service. Scales REST apart from the silos. |
| `api.replicas` | `2` | REST-only replicas when `api.enabled=true`. |
| `worker.enabled` | `false` | Set `true` to run script tasks and the read-model projector on their own pods. While `false`, the core silos run them. |
| `worker.replicas` | `1` | Worker silo count when `worker.enabled=true`. |
| `worker.nodeSelector` / `worker.tolerations` | `{}` / `[]` | Pin worker silos to spot/cheaper nodes. |
| `plugins.enabled` | `false` | Set `true` to add the [`fleans-plugins`](https://github.com/nightBaker/fleans-plugins) worker (`ghcr.io/nightbaker/fleans-plugins`) with every ready-made plugin — REST Caller and Camunda 7 external tasks. It connects to the API over gRPC and joins no cluster. |
| `plugins.replicas` | `1` | Worker replicas when `plugins.enabled=true`. |
| `plugins.image.repository` / `plugins.image.tag` | `ghcr.io/nightbaker/fleans-plugins` / chart `appVersion` | Point at your own worker image (built from the [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) template) to run your own plugins instead. |
| `plugins.env` | `[]` | Extra env for this worker only. The Camunda plugin needs `ConnectionStrings__camunda-external-tasks`; with `postgres.enabled` the chart points it at a `camunda_external_tasks` database on the bundled server. |
| `web.enabled` | `true` | Disable to ship a headless cluster (no admin UI). |
| `web.replicas` | `1` | Blazor Server is sticky-session-friendly; HA needs careful affinity. |
| `web.service.port` | `8080` | Service port for the UI. |
| `mcp.enabled` | `true` | Disable when no MCP-aware AI agents will connect. |
| `mcp.service.port` | `5200` | MCP server port. |
| `ingress.enabled` | `false` | Toggle the chart's `Ingress` for the admin UI. |
| `ingress.className` | `""` | e.g. `nginx`, `traefik`. Empty inherits the cluster default. |
| `ingress.host` | `fleans.example.com` | Public hostname. |
| `ingress.tls.enabled` | `false` | Set `true` + `ingress.tls.secretName` to terminate TLS. |
| `persistence.provider` | `Postgres` | `Postgres` (production) or `Sqlite` (dev parity, single-replica only — see chart-side caveat). |
| `postgres.enabled` | `true` | Set `false` to bring your own Postgres (see §4). |
| `postgres.image.tag` | `"16"` | Stays in sync with the test matrix in `Fleans.Persistence.Tests`. |
| `postgres.database` | `fleans` | DB name created on first boot. |
| `postgres.username` | `fleans` | Workflow user. |
| `postgres.password` | `""` | Empty = chart auto-generates and stores in a `Secret`. |
| `postgres.existingSecret` | `""` | Reference an existing `Secret` with key `postgres-password`. |
| `postgres.maxConnections` | `250` | The bundled server's `max_connections`. `helm template` fails when the pods' pools can exceed it (see [Connection budget](/fleans/reference/persistence/#connection-budget)). |
| `persistence.postgres.maxPoolSize.<workload>` | core `20`, worker `10`, api/web/mcp `5`, plugins `10` | Npgsql `Maximum Pool Size` per pod, appended to the bundled Postgres connection strings. `0` = Npgsql's default of 100. |
| `postgres.persistentVolume.enabled` | `true` | Disable for ephemeral data; persistence requires a `StorageClass`. |
| `postgres.persistentVolume.size` | `8Gi` | Size the PVC. |
| `redis.enabled` | `true` | Needed while `clustering.provider`, `reminders.provider` or `streaming.provider` is `Redis` (the defaults). Set `false` once none is — see [Running without Redis](#running-without-redis). |
| `redis.image.tag` | `"7-alpine"` | Pinned. |
| `redis.persistentVolume.enabled` | `false` | Off by default. Redis writes its AOF file, but without this volume the file lives in the container, so a Redis pod restart loses the membership table, every pending BPMN timer and undelivered stream events. Turn it on, or move reminders and streams off Redis, in production — see [Redis durability](#redis-durability). |
| `auth.authority` | `""` | OIDC issuer URL, stamped on the Web, core, `api` and `mcp` pods. Leaving empty disables auth everywhere. |
| `auth.clientId` | `""` | Web admin UI OIDC client ID. |
| `auth.clientSecretExistingSecret` | `""` | Reference a `Secret` with key `client-secret`. **Use this in production.** |
| `auth.clientSecret` | `""` | Inline OIDC client secret. **Not recommended for production.** |
| `assistant.endpoint` | `""` | Base URL of an [assistant backend](/fleans/reference/assistant-backend-contract/) for the editor's AI panel (`Fleans__Assistant__Endpoint` on the Web pod). Empty hides the panel. Fleans does not deploy a backend. |
| `streaming.provider` | `Redis` | `Redis` (chart-bundled Redis), `Kafka` or `AzureQueue` (durable), or `Memory` (single silo only). |
| `streaming.redis.totalQueueCount` | `8` | Orleans pulling-agent count for Redis streaming. |
| `streaming.kafka.brokers` | `""` | Comma-separated Kafka brokers, e.g. `kafka.kafka.svc:9092`. |
| `streaming.kafka.queueCount` | `8` | Orleans pulling-agent count for Kafka streaming. |
| `streaming.kafka.security.protocol` | `""` | `Plaintext`, `Ssl`, `SaslPlaintext` or `SaslSsl`; empty = plaintext. See [Kafka security](/fleans/reference/streaming/#kafka-security-sasl--tls). |
| `streaming.kafka.security.saslMechanism` / `saslUsername` | `""` | `Plain`, `ScramSha256` or `ScramSha512`, and the SASL user. |
| `streaming.kafka.security.saslPasswordSecret` / `saslPasswordSecretKey` | `""` / `password` | `Secret` holding the SASL password. |
| `streaming.kafka.security.tlsSecret` | `""` | `Secret` with `ca.crt` (and `tls.crt` / `tls.key`), mounted at `/etc/fleans/kafka`. Needs `Ssl` or `SaslSsl`. |
| `streaming.kafka.security.clientCertificate` | `false` | `true` presents `tls.crt` / `tls.key` to the broker (mutual TLS). |
| `reminders.provider` | `Redis` | Where BPMN timers (Orleans reminders) live: `Redis` (chart-bundled Redis) or `Postgres` (recommended in production; needs `persistence.provider=Postgres`). See §4. |
| `reminders.postgres.existingSecret` | `""` | `Secret` with the connection string of a dedicated reminders database (`ConnectionStrings__fleans-reminders`). Empty = share the `fleans` database. |
| `reminders.postgres.existingSecretKey` | `connection-string` | Key inside that `Secret`. |
| `clustering.provider` | `Redis` | Orleans membership table: `Redis` (chart-bundled Redis) or `Postgres` (recommended in production; needs `persistence.provider=Postgres`). Set on silos, `web` and `mcp`. See §4. |
| `clustering.postgres.existingSecret` | `""` | `Secret` with the connection string of a dedicated membership database (`ConnectionStrings__fleans-cluster`). Empty = share the `fleans` database. |
| `clustering.kubernetes.enabled` | `true` | Orleans Kubernetes hosting: a deleted silo pod leaves the cluster at once. Creates a ServiceAccount + Role (`pods`: `get`, `list`, `watch`, `patch`). |
| `clustering.serviceId` / `clustering.clusterId` | `fleans` / release fullname | `Orleans__ServiceId` / `Orleans__ClusterId`, and the `orleans/*` labels on silo pods. |
| `clustering.terminationGracePeriodSeconds` | `60` | SIGTERM → SIGKILL window for silo pods; the graceful silo stop gets 10 s less. Minimum 15. |
| `loadShedding.enabled` / `loadShedding.cpuThreshold` | `false` / `95` | Orleans load shedding on the silos: above the CPU threshold a silo rejects new requests from Orleans clients (`api`, `web`, `mcp`); the REST API answers 503. |
| `extraEnv` | `[]` | Extra env vars on `core`, `worker`, `api`, `web` and `mcp` (not `plugins`, which has `plugins.env`). Use it for external connection strings and any [configuration key](/fleans/reference/configuration/) the chart has no value for. |

To tune Orleans consumer parallelism (pulling-agent count per cluster) for higher throughput, set `Fleans__Streaming__Redis__TotalQueueCount` (Redis), `Fleans__Streaming__Kafka__QueueCount` (Kafka), or supply an explicit `Fleans__Streaming__AzureQueue__QueueNames__0..N` list — see [Tuning throughput](/fleans/reference/streaming/#tuning-throughput) for the sizing heuristic, rehash caveat, and the one-partition-per-topic rule for Kafka.

Object names, images and ports per workload: [What the chart deploys](#what-the-chart-deploys).

## 4. Production checklist

Each item below maps to a `values.yaml` override. Combine into a single
`values-prod.yaml` overlay and pass with `-f`.

<figure class="arch-diagram" style="--arch-ratio: 1390 / 646">
  <iframe data-arch-src="/fleans/diagrams/k8s-deployment.html" src="/fleans/diagrams/k8s-deployment.html?embed=1" title="Kubernetes production topology" loading="lazy"></iframe>
  <figcaption>The production topology this checklist builds toward: TLS ingress, Core and Worker silos in one Orleans cluster, managed PostgreSQL outside the namespace. <a href="/fleans/diagrams/k8s-deployment.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

### External Postgres (managed RDS / Cloud SQL / Aiven)

```yaml
postgres:
  enabled: false
persistence:
  provider: Postgres
extraEnv:
  - name: ConnectionStrings__fleans
    valueFrom:
      secretKeyRef:
        name: fleans-pg
        key: connection-string
```

Create the `Secret` separately (out-of-band or via your secrets operator):

```bash
kubectl create secret generic fleans-pg \
  -n fleans \
  --from-literal=connection-string='Host=my-pg.example.com;Port=5432;Database=fleans;Username=fleans;Password=...;Ssl Mode=Require;Maximum Pool Size=20'
```

Keep `Maximum Pool Size` in the string. Without it, each pod's pool can grow to 100 connections, and a few replicas exhaust the server's `max_connections`. Size it with the [connection budget](/fleans/reference/persistence/#connection-budget). The chart does not check an external server's limit.

The Postgres user needs `CREATE TABLE` on first boot (for migrations), then
only `INSERT/UPDATE/SELECT/DELETE` thereafter.

### Redis durability

On a default install the bundled Redis holds the Orleans membership table, every pending
BPMN timer and the stream queues, with no volume (`redis.persistentVolume.enabled=false`).
A Redis pod restart — eviction, node drain, OOM kill — then loses the pending timers and
undelivered stream events: the instances waiting on them stall, and nothing registers the
timers again. Before production, do one of:

```yaml
redis:
  persistentVolume:
    enabled: true        # keep the AOF file across Redis pod restarts
```

or move the timers (and streams) off Redis: `reminders.provider=Postgres` (next section)
and `streaming.provider=Kafka` or `AzureQueue`. A volume protects against a pod restart,
not against losing the volume; the bundled Redis is a single pod without replication.

### Backups

Back up the PostgreSQL databases (and the Redis volume while timers live there) on a schedule, and
test a restore. Procedure: [Backup and restore](/fleans/guides/backup-restore/).

### Timers (reminders) on their own Postgres database

Every pending BPMN timer is one Orleans reminder. The default `Redis` provider keeps all of
them in one Redis key; once pending timers reach the millions (e.g. boundary timers on
long-running user tasks), move them to PostgreSQL in their own database:

```yaml
reminders:
  provider: Postgres
  postgres:
    existingSecret: fleans-reminders-pg
```

```bash
kubectl create secret generic fleans-reminders-pg \
  -n fleans \
  --from-literal=connection-string='Host=my-pg.example.com;Port=5432;Database=fleans_reminders;Username=fleans;Password=...;Ssl Mode=Require'
```

Create the database first; Fleans creates its tables on first boot. Every silo re-reads its
share of the reminders every 5 minutes, so a separate database (on the same server or its own)
keeps those reads off the workflow journal and read model. Without `existingSecret` the
reminders share the `fleans` database. Custom-task workers are not silos and have no reminder settings.
Switching providers starts with an empty reminder table: pending timers from the old provider
do not move over.

### Clustering and Kubernetes hosting

The Orleans membership table decides which silo is alive. The default `Redis` provider uses the
chart-bundled Redis, whose replication is asynchronous: a Redis failover can drop a membership
write. In production put membership on a highly-available PostgreSQL (e.g. Patroni):

```yaml
clustering:
  provider: Postgres
  postgres:
    existingSecret: fleans-cluster-pg   # optional: own database; empty = share `fleans`
```

Create the database first; Fleans creates its tables on first boot. While the membership
database is unreachable no silo can join and none can be declared dead, so give it the same
availability as the workflow database. Custom worker hosts must use the same provider.

`clustering.kubernetes.enabled` (on by default) turns on Orleans Kubernetes hosting: when a silo
pod is deleted — scale-down, eviction, node loss — the silo is removed from membership at once,
and grain calls stop routing to it. The chart grants the silo pods' ServiceAccount `get`, `list`,
`watch` and `patch` on `pods` in the release namespace, labels them `orleans/serviceId` /
`orleans/clusterId`, and names the silo Deployments `core-<release>` / `worker-<release>`: with
Kubernetes hosting the pod name is the silo name, and Fleans reads the silo's role from its
prefix. Turn it off (`false`) if your cluster forbids that Role; membership then falls back to
Orleans' probe-based failure detection.

### REST API apart from the silos

By default the `core` silo also serves the REST API, so adding REST capacity adds silos.
`api.enabled=true` adds a `<release>-api` Deployment and Service: the same `fleans-api` image
with `Fleans__Role=Api`, which runs as an Orleans client with no grains. Scale it on requests per
second with `api.replicas`, and point REST clients (and your ingress, if you expose the API) at
the `<release>-api` Service. It joins the cluster through the same membership table as
`web` and `mcp`.

```yaml
api:
  enabled: true
  replicas: 3
```

### Running without Redis

Redis is only needed while clustering, reminders or streaming uses it. With all three on other
providers, turn the bundled Redis off. The workloads then register no Redis client and no Redis
health check:

```yaml
redis:
  enabled: false
persistence:
  provider: Postgres
clustering:
  provider: Postgres
reminders:
  provider: Postgres
streaming:
  provider: Kafka          # or AzureQueue
  kafka:
    brokers: kafka.kafka.svc:9092
```

Without Redis the admin UI keeps its sign-in cookie keys (ASP.NET Core Data Protection) inside
the pod. With OIDC enabled, run a single `web` replica.

### Load shedding

```yaml
loadShedding:
  enabled: true
  cpuThreshold: 85
```

Above the threshold a silo's gateway rejects new requests from Orleans clients (`api`, `web`,
`mcp`) instead of queueing them, and the REST API answers `503`. Calls between silos are not
shed. It pairs with the REST rate limiter, which answers `429` before a request reaches the
cluster.

### Backpressure and autoscaling

Load shedding alone does not keep an overloaded cluster alive: it rejects only new client
requests, while the work that saturates a cluster is already inside (stream deliveries,
activations). What does is admission at the edge. Every `api` pod runs an **adaptive admission
limit** on the start / message / signal / task endpoints, on by default: when the p95 latency of
those requests passes 500 ms it admits fewer at once, and the rest get `429` with
`Retry-After: 1` instead of a timeout. Tune it with `extraEnv`:

```yaml
extraEnv:
  - name: Fleans__Admission__TargetLatencyMs   # about 2 × the p99 of a start at normal load
    value: "500"
```

All keys: [Configuration → Admission control](/fleans/reference/configuration/#admission-control).
Clients must retry a `429` with back-off. An autoscaler has time to add silos only while the
cluster still answers, so scale on CPU or the `Fleans` meter well below saturation. If you add a
fixed `RateLimiting` limit as well, size it at about 70–75 % of the throughput you measured:
the rate a cluster sustains under contention is lower than the peak it reaches when idle.

Silo pods get `terminationGracePeriodSeconds: 60` so a silo can leave the cluster and hand off its
grains and timers before Kubernetes kills it.

### Authentication (OIDC for the admin UI, JWT for REST + MCP)

```yaml
auth:
  authority: https://idp.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc   # Secret with key `client-secret`
extraEnv:
  # Behind the chart's Ingress: trust X-Forwarded-Proto / -For from the ingress controller's
  # pods, so the admin UI builds its OIDC redirect URI as https://<ingress.host>/signin-oidc.
  - name: Authentication__KnownNetworks__0
    value: "10.0.0.0/8"                     # your cluster's pod CIDR
```

The chart wires the Web pods' env from this `Secret`. The core, `api` and `mcp` pods get
`auth.authority` too and then require JWT bearer tokens — audience `fleans-api` for REST,
`fleans-mcp` for MCP, so have your IdP issue both. With auth on, the `plugins` worker needs a
token as well: set `Fleans__ExternalTaskWorker__AccessToken` in `plugins.env`.

The admin UI honours forwarded headers only from the addresses in
`Authentication:KnownProxies` / `Authentication:KnownNetworks`, and its session cookie is
`Secure`-only. Behind a TLS-terminating Ingress, without one of them the UI sees plain
HTTP from the controller, builds an `http://` redirect URI, and the IdP rejects it. List more ranges as `__1`, `__2`, … The chart's `auth` block has no value for these keys,
hence `extraEnv`. See [Authentication](/fleans/reference/authentication/) for the OIDC scopes
the admin UI requests, the API's JWT validation contract, and the
[role claim](/fleans/reference/authorization/).

### Ingress with TLS via cert-manager

```yaml
ingress:
  enabled: true
  className: nginx
  host: fleans.example.com
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-prod
  tls:
    enabled: true
    secretName: fleans-tls
```

cert-manager populates `fleans-tls` via the `cert-manager.io/cluster-issuer`
annotation; the chart references the secret in the `Ingress`'s `tls` block.

### Dedicated worker silos

To run script tasks and the read-model projector on their own nodes:

```yaml
worker:
  enabled: true
  replicas: 2
  nodeSelector:
    workload: fleans-worker
  tolerations:
    - key: dedicated
      value: fleans-worker
      effect: NoSchedule
```

### Resource requests / limits

The chart's defaults assume a small workload. For production load, raise
`core.replicas` (or enable [autoscaling](#autoscaling)) and bump CPU/memory based on the
silos' CPU and the Fleans metrics. See [Observability](/fleans/reference/observability/) for
the metrics that matter, and
[Recommended for scale → Sizing and scaling](/fleans/guides/topology-scalable/#sizing-and-scaling)
for per-unit numbers.

### Autoscaling

Core and Worker silos can scale on CPU with a `HorizontalPodAutoscaler`; the Deployment then omits `replicas`. Utilization is relative to the CPU request, so set the request to what one silo really uses:

```yaml
core:
  resources:
    requests: { cpu: "1", memory: 1Gi }
  autoscaling:
    enabled: true
    minReplicas: 3
    maxReplicas: 12
    targetCPUUtilizationPercentage: 65
worker:
  enabled: true
  autoscaling:
    enabled: true
```

With [KEDA](https://keda.sh) and Prometheus in the cluster, `core.autoscaling.keda` (and `worker.autoscaling.keda`) renders a KEDA `ScaledObject` instead: the same CPU target plus Prometheus triggers on Fleans' own metrics, for example the journal append p99 (`fleans_journal_append_duration_seconds`) or the Kafka consumer lag (`fleans_kafka_consumer_lag`) — see the chart README for a full example.

Scale-down is deliberately slow (one silo per 2 minutes after a 5-minute window): each removed silo hands its grains and its reminder range to the others. Keep the target at or below ~70 %: past the CPU ceiling a Fleans cluster stops rather than degrades, so also put a rate limit on the REST entry point (`RateLimiting:WorkflowMutation`) below the measured capacity.

### Image-pull secrets (private registry mirroring)

```yaml
imagePullSecrets:
  - name: my-registry-pull
```

### Security defaults

What a default install leaves open, and what it hardens:

- **Authentication is off** while `auth.authority` is empty. The REST API, the gRPC
  external-task endpoint, the MCP server and the admin UI then accept anonymous requests,
  and every [role check](/fleans/reference/authorization/) allows everyone. `/health` and
  `/alive` are anonymous in every mode. The Orleans Dashboard is not served at all until the
  admin UI has OIDC configured.
- **Nothing is exposed outside the cluster**: every Service is `ClusterIP`, and the optional
  Ingress routes only to the admin UI. Expose the REST API with your own Ingress or Gateway
  rule to `<fullname>-core` (or `<fullname>-api`).
- **The bundled Redis has no password**, and the chart creates no `NetworkPolicy`: any pod in
  the cluster can reach Redis, PostgreSQL and the silos' Orleans ports, and Orleans traffic
  between pods is not encrypted. Add a `NetworkPolicy` that admits only the release's pods.
- **The bundled PostgreSQL** gets a random password in the `<fullname>-postgres` Secret,
  unless you set `postgres.password` or `postgres.existingSecret`.
- **Pods are hardened**: every pod runs as UID 1000 (`runAsNonRoot`), with
  `allowPrivilegeEscalation: false` and all Linux capabilities dropped
  (`podSecurityContext` / `securityContext`). The root filesystem stays writable.

## 5. New releases and rollbacks

Until 1.0.0 a new release starts on fresh, empty stores: `helm upgrade` to a new chart
version is not supported, because the data of the old release is not converted. Running
instances and deployed definitions do not carry over. The steps (drain, back up, uninstall,
delete the kept PVC and Secret, install) are in
[Upgrading](/fleans/guides/upgrading/#helm-chart).

Within one release, `helm upgrade` with the same chart and a changed values file is a rolling
restart of the changed workloads — see
[High availability → Rolling restarts](/fleans/reference/high-availability/#rolling-restarts).
Some values cannot change on a running cluster; see
[What cannot change on a live cluster](/fleans/guides/topology-scalable/#what-cannot-change-on-a-live-cluster).
To undo such a values change:

```bash
helm history fleans -n fleans
helm rollback fleans <REVISION> -n fleans
```

## 6. Troubleshooting

- **`ImagePullBackOff`.** Either the registry is private and `imagePullSecrets`
  is missing, or the tag doesn't exist. `kubectl describe pod <name>`
  shows the underlying error.
- **`CrashLoopBackOff` on a Fleans pod with a Postgres connection error.**
  When `postgres.enabled=false`, the `extraEnv` `ConnectionStrings__fleans`
  must resolve before the pods start. Check the referenced `Secret` via
  `kubectl get secret fleans-pg -o jsonpath='{.data.connection-string}' | base64 -d`.
- **OIDC `redirect_uri_mismatch` from the IdP.** Add the ingress host
  (e.g. `https://fleans.example.com/signin-oidc`) to the IdP's allowed
  redirect URIs. If the IdP reports an `http://` redirect URI, the admin UI
  does not trust the ingress controller's forwarded headers: set
  `Authentication__KnownNetworks__0` (or `Authentication__KnownProxies__0`) in
  `extraEnv`, as in [Authentication](#authentication-oidc-for-the-admin-ui-jwt-for-rest--mcp).
- **Live-edit drift after `kubectl edit`.** Helm tracks the rendered manifest;
  in-cluster edits get reverted on the next `helm upgrade`. Inspect
  effective values with `helm get values fleans -n fleans`.
- **Silo pod `CrashLoopBackOff` with "pod … must start with 'core-'".**
  Kubernetes hosting is on and the pod was not created by the chart's role-prefixed
  Deployment (e.g. a hand-written manifest). Name the Deployment `core-…` / `worker-…`, or set
  `clustering.kubernetes.enabled=false`.
- **Kubernetes hosting logs `Forbidden` / insufficient permissions.** The silo ServiceAccount
  lacks the pod Role — check `kubectl auth can-i watch pods --as=system:serviceaccount:<ns>:<release>-silo`.
- **HPA fights Orleans placement.** Orleans grains stick to silos; an HPA
  scaling Core or Worker silos in/out can churn placement. Set
  `core.replicas` / `worker.replicas` explicitly for stable workloads.

## See also

- [Upgrading](/fleans/guides/upgrading/) — moving to a new release before 1.0.
- [Deployment](/fleans/reference/deployment/) — ports per process and raw
  manifests via `helm template`.
- [Configuration](/fleans/reference/configuration/) — full configuration
  matrix (env-var → `values.yaml` mapping).
- [Persistence](/fleans/reference/persistence/) — Postgres + Sqlite providers,
  external-Postgres connection-string contract.
- [Authentication](/fleans/reference/authentication/) — OIDC scopes, JWT
  validation, the cookie + bearer flows.
- [Streaming](/fleans/reference/streaming/) — Kafka provider configuration
  and production-readiness caveat.
- [Cutting a Release](https://github.com/nightBaker/fleans/blob/main/docs/runbooks/release.md) —
  maintainer runbook for publishing a new chart tarball.
