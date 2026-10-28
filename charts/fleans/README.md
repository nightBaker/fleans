# Fleans Helm chart

Deploy the [Fleans](https://nightbaker.github.io/fleans/) BPMN workflow engine on Kubernetes.

## What this chart deploys

| Component | Image | Purpose |
| --------- | ----- | ------- |
| `core-<fullname>` Deployment | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Core`). Hosts coordinator grains. |
| `worker-<fullname>` Deployment (optional) | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Worker`). Hosts `[StatelessWorker]` script/condition grains. Same image as `core` — different role env var. |
| `<fullname>-api` Deployment + Service (optional, `api.enabled`) | `ghcr.io/nightbaker/fleans-api:<tag>` | REST only (`Fleans__Role=Api`): an Orleans client with no grains, scaled apart from the silos. |
| `web` Deployment (optional, default on) | `ghcr.io/nightbaker/fleans-web:<tag>` | Blazor Server admin UI. |
| `mcp` Deployment (optional, default on) | `ghcr.io/nightbaker/fleans-mcp:<tag>` | MCP server (port 5200) for AI-agent integration. |
| `redis` StatefulSet | `redis:7-alpine` | Orleans clustering, reminders and streams when their provider is Redis (the defaults). `redis.enabled=false` once none of them is. |
| `<fullname>-silo` ServiceAccount + Role + RoleBinding | — | Orleans Kubernetes hosting (`clustering.kubernetes.enabled`, default on): pods `get`/`list`/`watch`/`patch`. |
| `postgres` StatefulSet (optional) | `postgres:16` | Workflow persistence when `persistence.provider=Postgres`. |

> **Note on the worker.** `Fleans.Worker` is a class library hosted by `Fleans.Api`, not a separate executable. Both Core and Worker silos run the same `fleans-api` image and differ only by the `Fleans__Role` env var. There is no separate `fleans-worker` image.

## Prerequisites

- Kubernetes 1.25+
- Helm 3.12+
- A `StorageClass` if you enable persistent volumes (default for Postgres, off for Redis).
- Pull access to `ghcr.io/nightbaker/*` (public for the Fleans repo's published images; provide `imagePullSecrets` if you mirror to a private registry).

## Quick install — kind (local laptop)

```bash
# 1. Create a single-node test cluster.
kind create cluster --name fleans-test

# 2. Lint the chart.
helm lint charts/fleans/

# 3. Render templates locally (no cluster contact) to sanity-check the manifests.
helm template fleans charts/fleans/ --debug | head -120

# 4. Install. Disable Postgres persistence + ingress for the smoke test.
helm install fleans charts/fleans/ \
  --set postgres.persistentVolume.enabled=false \
  --set ingress.enabled=false

# 5. Wait for everything to come up.
kubectl get pods -l app.kubernetes.io/instance=fleans -w
# (Ctrl-C when all show 1/1 Running.)

# 6. Reach the admin UI.
kubectl port-forward svc/fleans-web 8080:8080
# open http://localhost:8080/

# 7. Tear down.
helm uninstall fleans
kind delete cluster --name fleans-test
```

## Common configuration

### Pin to a specific image tag

`values.yaml` defaults `image.tag` to the chart's `appVersion`. Override on the command line for nightlies / hotfixes:

```bash
helm install fleans charts/fleans/ --set image.tag=0.1.0-rc.2
```

### External Postgres

`persistence.provider` accepts `Postgres` (default) or `Sqlite` (case-insensitive); other values abort `helm template`.

Disable the in-cluster Postgres and supply a connection string via `extraEnv`:

```yaml
postgres:
  enabled: false
persistence:
  provider: Postgres
extraEnv:
  - name: ConnectionStrings__fleans
    valueFrom:
      secretKeyRef:
        name: my-existing-pg-secret
        key: connection-string
```

### Separate journal database

The workflow journal (event batches + snapshots) uses the `fleans` database unless you point it elsewhere — for example at a Citus cluster that shards it by instance id. Add the journal's connection string (and, for Citus, the provider) via `extraEnv`:

```yaml
extraEnv:
  - name: Persistence__Journal__Provider
    value: Citus            # or Postgres for a plain separate database
  - name: Persistence__Journal__Citus__ShardCount
    value: "128"            # optional; fixed once the journal tables are distributed
  - name: ConnectionStrings__fleans-journal
    valueFrom:
      secretKeyRef:
        name: my-journal-db-secret
        key: connection-string
```

The silos apply the journal's migrations to that database at startup. See the [persistence reference](https://nightbaker.github.io/fleans/reference/persistence/#separate-journal-database).

### OIDC for the admin UI

```yaml
auth:
  authority: https://idp.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc   # Secret with key `client-secret`
```

Leave `auth.authority` empty to ship without authentication (single-tenant / dev).

### Streaming provider

Set `streaming.provider` to one of `Memory`, `Redis`, `Kafka`, or `AzureQueue` (case-insensitive). Default: `Redis` (reuses the chart-bundled Redis, no extra infrastructure). Streams are implicit-only, so no store holds subscription state.

> **WARNING:** `Memory` is single-silo debug-only. Multi-silo deployments must NOT use Memory — events published on one silo will not reach handlers on others.

| Provider | When to use | Extra config |
|----------|-------------|---------------|
| `Redis` (default) | The common case. Reuses the chart-bundled Redis. | None. |
| `Memory` | Single-silo dev/test only. | None. |
| `Kafka` | You already operate Kafka and want streams there. | `streaming.kafka.brokers` (comma-separated `host:port` list). |
| `AzureQueue` | Azure-native deployments using Storage Queue or Azurite. | `streaming.azureQueue.connectionString` (inline; production should use the `existingSecret` pattern — follow-up tracked separately). |

Unknown values abort `helm template` at render time with a message naming the valid providers — typos can't silently ship.

```yaml
# Redis (default — usually nothing to set)
streaming:
  provider: Redis

# Kafka
streaming:
  provider: Kafka
  kafka:
    brokers: "kafka.kafka.svc:9092"

# AzureQueue
streaming:
  provider: AzureQueue
  azureQueue:
    connectionString: "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=..."
```

### Reminders provider

Every pending BPMN timer is one Orleans reminder. Set `reminders.provider` to `Redis` (default — reuses the chart-bundled Redis) or `Postgres` (case-insensitive). **Postgres is the recommended production provider** once pending timers reach the millions: the Redis provider keeps every reminder of the cluster in one Redis key. `Postgres` requires `persistence.provider=Postgres`; other values abort `helm template`.

Point the reminders at their own database so their periodic range reads (every silo re-reads its slice of the reminder ring every 5 minutes) do not compete with the journal and read model. Without `existingSecret` they share the `fleans` database.

```yaml
reminders:
  provider: Postgres
  postgres:
    existingSecret: fleans-reminders-db   # Secret with the connection string of the reminders database
    existingSecretKey: connection-string
```

Custom worker hosts must be built with the same reminder provider — every silo serves a slice of the reminder ring. Tune Orleans `ReminderOptions` with `extraEnv`, e.g. `Fleans__Reminders__ReminderLoadingWindow=00:30:00`.

### Clustering provider and Kubernetes hosting

The Orleans membership table decides which silo is alive. Set `clustering.provider` to `Redis` (default — reuses the chart-bundled Redis) or `Postgres` (case-insensitive). **Postgres is the recommended production provider**: Redis replication is asynchronous, so a Redis failover can drop an acknowledged membership write, while Postgres commits it durably (run it highly available, e.g. Patroni). `Postgres` requires `persistence.provider=Postgres`; other values abort `helm template`. The value is set on the silos *and* on `web`/`mcp`, whose gateway list is the same table.

```yaml
clustering:
  provider: Postgres
  postgres:
    existingSecret: fleans-cluster-db   # optional: own database (ConnectionStrings__fleans-cluster)
    existingSecretKey: connection-string
```

`clustering.kubernetes.enabled` (default `true`) turns on Orleans Kubernetes hosting: a silo whose pod is deleted leaves the membership table straight away instead of after the probe window. The chart then

- creates the `<fullname>-silo` ServiceAccount with a Role on `pods` (`get`, `list`, `watch`, `patch`);
- labels silo pods `orleans/serviceId` / `orleans/clusterId` (`clustering.serviceId`, `clustering.clusterId` — default the release fullname) and passes `POD_NAME` / `POD_NAMESPACE` / `POD_IP` through the downward API;
- names silo Deployments `<role>-<fullname>` (`core-…`, `worker-…`, `worker-…-custom`): Kubernetes hosting makes the pod name the silo name, and Fleans placement reads the role from its prefix.

Silo pods get `terminationGracePeriodSeconds: 60` (`clustering.terminationGracePeriodSeconds`, minimum 15); the silo's graceful stop gets 10 s less. Custom worker hosts must use the same clustering provider.

### REST API apart from the silos

`api.enabled=true` adds `<fullname>-api`: the `fleans-api` image with `Fleans__Role=Api`, an Orleans client that hosts no grains and only serves REST. Scale it on requests per second (`api.replicas`, default 2) without adding silos; point REST clients at the `<fullname>-api` Service. The core silo keeps serving REST too.

### Running without Redis

Redis is only needed while one of clustering, reminders or streaming uses it. With all three elsewhere, turn the bundled Redis off; the hosts then register no Redis client and no Redis health check:

```yaml
redis:
  enabled: false
clustering:
  provider: Postgres
reminders:
  provider: Postgres
streaming:
  provider: Kafka        # or AzureQueue
  kafka:
    brokers: kafka.kafka.svc:9092
```

Without Redis the admin UI keeps its Data Protection keys (sign-in cookies) in the pod, so with OIDC on run one `web` replica.

### Load shedding

`loadShedding.enabled=true` sets `Fleans__LoadShedding__Enabled` / `__CpuThreshold` (`loadShedding.cpuThreshold`, default 95) on the silos: above that CPU a silo's gateway rejects new client requests, which the REST API answers with 503, instead of queueing them. It acts on requests from Orleans clients (`api`, `web`, `mcp`), not on REST calls a core silo serves itself.

### Worker silos on dedicated nodes

```yaml
worker:
  enabled: true
  replicas: 3
  nodeSelector:
    node-role: fleans-worker
  tolerations:
    - key: spot-instance
      operator: Exists
      effect: NoSchedule
```

### Read-model projector

The projector builds the read model a few seconds behind each instance's journal. It runs on the worker pods (`worker.enabled=true`), next to the script executor, so projection writes do not share CPU with instance execution; without workers it runs on the core silos. To serve reads from a PostgreSQL streaming replica, add `ConnectionStrings__fleans-query` to `extraEnv`: the projector keeps writing the primary, while the admin UI, MCP and the REST listings read the replica (see the website's Persistence reference).

### Autoscaling Core and Worker silos

`core.autoscaling` / `worker.autoscaling` add a `HorizontalPodAutoscaler` on CPU (`templates/hpa.yaml`); the Deployment then omits `replicas`. Utilization is relative to `resources.requests.cpu`, so set the request to what one silo really uses:

```yaml
core:
  resources:
    requests: { cpu: "1", memory: 1Gi }
  autoscaling:
    enabled: true
    minReplicas: 3
    maxReplicas: 12
    targetCPUUtilizationPercentage: 65   # p99 stays flat below ~80 % in the load tests
```

Scale-down removes one silo per 2 minutes after a 5-minute window (`scaleDownStabilizationSeconds`): each removed silo moves its activations and its reminder range.

#### Autoscaling on Fleans metrics (KEDA)

With [KEDA](https://keda.sh) installed and a Prometheus that scrapes the silos' metrics (OTLP → collector → Prometheus), `autoscaling.keda.enabled` renders a `ScaledObject` instead of the HPA: the same CPU target plus Prometheus triggers on the `Fleans` meter — scale out on whichever trigger asks for more pods.

```yaml
core:
  autoscaling:
    enabled: true
    keda:
      enabled: true
      serverAddress: http://prometheus-server.monitoring.svc
      prometheus:
        # Journal append p99 above 50 ms: the journal or the silos are saturating.
        - name: journal-append-p99
          query: histogram_quantile(0.99, sum by (le) (rate(fleans_journal_append_duration_seconds_bucket[2m])))
          threshold: "0.05"
        # Stream backlog: records behind the head, summed over partitions.
        - name: kafka-consumer-lag
          query: sum(fleans_kafka_consumer_lag)
          threshold: "1000"
```

The thresholds are per pod (KEDA divides the query's value by the threshold to get the replica count), so pick queries that grow with the backlog. Projector lag (`fleans_projection_lag_seconds`) belongs on `worker.autoscaling.keda`: the projector runs on the worker pods.

## Upgrade notes

- **`customWorker` was replaced by `plugins` (0.9.0).** `plugins.enabled=true` runs the `ghcr.io/nightbaker/fleans-plugins` host (Plugin role, Deployment `plugin-<fullname>`); set `plugins.image.*` to run your own plugin host image instead.

- **Silo Deployments were renamed (#885).** `<fullname>-core` / `-worker` / `-custom-worker` are now `core-<fullname>` / `worker-<fullname>` / `worker-<fullname>-custom`; `helm upgrade` replaces them (a full silo restart).

- **Postgres password is sticky.** First install generates a random password into a `Secret` (or uses `postgres.password` if you supply one). Subsequent `helm upgrade` calls reuse the existing Secret via the `lookup` function so you don't get locked out.
- **Persistent volumes are kept on uninstall.** The Postgres Secret carries `helm.sh/resource-policy: keep`. Delete the PVC and Secret manually for a clean reinstall.

## Verifying changes

```bash
helm lint charts/fleans/

# Render with main and your branch, then diff — useful in PRs to see what changed.
git stash
helm template fleans charts/fleans/ > /tmp/rendered-main.yaml
git stash pop
helm template fleans charts/fleans/ > /tmp/rendered-branch.yaml
diff -u /tmp/rendered-main.yaml /tmp/rendered-branch.yaml | less
```

## Acceptance reference (from #407)

| Criterion | Where to check |
| --------- | -------------- |
| Chart skeleton at `charts/fleans/` with `Chart.yaml`, `values.yaml`, `templates/` | this directory |
| Lints clean | `helm lint charts/fleans/` |
| Installs against kind | "Quick install — kind" section above |
| Image tag defaults to `appVersion` | `image.tag` default in `values.yaml`, fallback in `templates/_helpers.tpl::fleans.imageTag` |
| No secrets in repo | OIDC client-secret + Postgres password go through `Secret` (`existingSecret` or chart-managed); `values.yaml` defaults to placeholders |
