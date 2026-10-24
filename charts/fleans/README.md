# Fleans Helm chart

Deploy the [Fleans](https://nightbaker.github.io/fleans/) BPMN workflow engine on Kubernetes.

## What this chart deploys

| Component | Image | Purpose |
| --------- | ----- | ------- |
| `core-<fullname>` Deployment | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Core`). Hosts coordinator grains. |
| `worker-<fullname>` Deployment (optional) | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Worker`). Hosts `[StatelessWorker]` script/condition grains. Same image as `core` — different role env var. |
| `projector-<fullname>` Deployment (optional) | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Projector`). Hosts only the read-model projector, which builds instance lists and completed-instance views from the journal. Off by default: the projector then runs on the core silos. |
| `web` Deployment (optional, default on) | `ghcr.io/nightbaker/fleans-web:<tag>` | Blazor Server admin UI. |
| `mcp` Deployment (optional, default on) | `ghcr.io/nightbaker/fleans-mcp:<tag>` | MCP server (port 5200) for AI-agent integration. |
| `redis` StatefulSet | `redis:7-alpine` | Orleans clustering, reminders and streams when their provider is Redis (the defaults). Required. |
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
- names silo Deployments `<role>-<fullname>` (`core-…`, `worker-…`, `projector-…`, `worker-…-custom`): Kubernetes hosting makes the pod name the silo name, and Fleans placement reads the role from its prefix.

Silo pods get `terminationGracePeriodSeconds: 60` (`clustering.terminationGracePeriodSeconds`, minimum 15); the silo's graceful stop gets 10 s less. Custom worker hosts must use the same clustering provider.

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

### Read-model projector on its own silos

```yaml
projector:
  enabled: true
  replicas: 2
```

The projector builds the read model a few seconds behind each instance's journal. With `projector.enabled` it moves from the core silos to `projector-<fullname>` pods, which host nothing else, so projection writes no longer share CPU with instance execution. To serve reads from a PostgreSQL streaming replica, add `ConnectionStrings__fleans-query` to `extraEnv`: the projector keeps writing the primary, while the admin UI, MCP and the REST listings read the replica (see the website's Persistence reference).

## Upgrade notes

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
