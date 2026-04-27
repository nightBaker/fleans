---
title: Self-Hosting on Kubernetes
description: Install Fleans on Kubernetes using the official Helm chart — components, configuration, and a kind-based smoke test.
sidebar:
  order: 5
---

Fleans ships an official Helm chart at [`charts/fleans/`](https://github.com/nightBaker/fleans/tree/main/charts/fleans) for self-hosting the engine on any Kubernetes cluster. Aspire is the recommended local-development experience; the Helm chart is the recommended path for staging, production, and any deployment where you control your own Kubernetes.

## Why a Helm chart?

Aspire orchestrates the full Fleans stack on a developer laptop, but it is not a deployment tool — it does not produce Kubernetes manifests for clusters you operate. The chart fills that gap with a single artifact that:

- Wires the Orleans silos (`Core` and optional `Worker`), the Blazor admin UI, and the MCP server together with the right service discovery and ports.
- Provisions Redis (required for Orleans clustering) and Postgres (default workflow persistence) inside the release, with the option to disable either and bring your own.
- Centralises all tunables — image tags, replica counts, OIDC settings, ingress, Kafka streaming wiring — in one `values.yaml`.

## What the chart deploys

| Component | Image | Purpose |
| --- | --- | --- |
| `core` Deployment | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Core`). Hosts coordinator grains. |
| `worker` Deployment (off by default) | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Worker`). Hosts `[StatelessWorker]` script/condition grains. **Same image as `core`** — distinguished only by the `Fleans__Role` env var. |
| `web` Deployment | `ghcr.io/nightbaker/fleans-web:<tag>` | Blazor Server admin UI on port `8080`. |
| `mcp` Deployment | `ghcr.io/nightbaker/fleans-mcp:<tag>` | MCP server on port `5200` for AI-agent integration. |
| `redis` StatefulSet | `redis:7-alpine` | Orleans clustering + grain storage. **Required.** |
| `postgres` StatefulSet (default on) | `postgres:16` | Workflow persistence when `persistence.provider=Postgres`. |

Optional resources: an `Ingress` for the admin UI, a chart-managed OIDC `Secret`, and Kafka streaming environment variables on the silos.

> **Note on the Worker silo.** `Fleans.Worker` is a class library hosted by `Fleans.Api`, not a separate executable. There is no `fleans-worker` image — both `core` and `worker` Deployments use `image.api`. Enabling `worker.enabled=true` lets Orleans place stateless script/condition grains on dedicated pods (and dedicated nodes via `worker.nodeSelector` / `worker.tolerations`) without pulling a second image.

## Prerequisites

- Kubernetes **1.25+**
- Helm **3.12+**
- A `StorageClass` if you enable persistent volumes (default for Postgres; off for Redis).
- Pull access to `ghcr.io/nightbaker/*` (public for the Fleans repo's published images; provide `imagePullSecrets` if you mirror to a private registry).

## Quick install — kind (local laptop)

This is the same smoke test the chart's CI runs. It installs the engine into a single-node `kind` cluster, port-forwards the admin UI, and tears everything down at the end.

```bash
# 1. Create a single-node test cluster.
kind create cluster --name fleans-test

# 2. From the repo root: lint and render before installing.
helm lint charts/fleans/
helm template fleans charts/fleans/ --debug | head -120

# 3. Install. Disable Postgres persistence + ingress for the smoke test.
helm install fleans charts/fleans/ \
  --set postgres.persistentVolume.enabled=false \
  --set ingress.enabled=false

# 4. Wait for everything to come up.
kubectl get pods -l app.kubernetes.io/instance=fleans -w
# (Ctrl-C when all show 1/1 Running.)

# 5. Reach the admin UI.
kubectl port-forward svc/fleans-web 8080:8080
# open http://localhost:8080/

# 6. Tear down.
helm uninstall fleans
kind delete cluster --name fleans-test
```

## Common configuration

### Pin to a specific image tag

`values.yaml` defaults `image.tag` to the chart's `appVersion`. Override on the command line for nightlies and hotfixes:

```bash
helm install fleans charts/fleans/ --set image.tag=0.1.0-rc.2
```

### Bring your own Postgres

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

See the [Persistence](./persistence/) reference for the connection-string contract and read-replica wiring.

### OIDC for the admin UI

```yaml
auth:
  authority: https://idp.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc   # Secret with key `client-secret`
```

Leave `auth.authority` empty to ship without authentication (single-tenant or dev). See the [Authentication](./authentication/) reference for full configuration semantics.

### Kafka streaming provider

```yaml
streaming:
  provider: Kafka
extraEnv:
  - name: Fleans__Streaming__Kafka__Brokers
    value: kafka.kafka.svc.cluster.local:9092
```

See the [Streaming](./streaming/) reference for provider details and at-least-once delivery semantics.

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

## A best-practice production install

A starting point for a real cluster — pinned tag, external managed Postgres, OIDC on the admin UI, ingress with TLS, and a small worker pool isolated to dedicated nodes. Save as `prod-values.yaml` and install with `helm install fleans charts/fleans/ -f prod-values.yaml`.

```yaml
image:
  tag: 0.1.0-beta            # pin — never let `latest` drift in prod

# Use a managed Postgres (RDS, Cloud SQL, Neon, …) — drop the in-cluster one.
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

# Three Worker silos on a dedicated node pool.
worker:
  enabled: true
  replicas: 3
  nodeSelector:
    workload: fleans-worker

# Admin UI behind your IdP and a TLS ingress.
auth:
  authority: https://id.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc
ingress:
  enabled: true
  className: nginx
  hosts:
    - host: fleans.example.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - secretName: fleans-tls
      hosts:
        - fleans.example.com
```

## Upgrade and lifecycle notes

- **Postgres password is sticky.** First install generates a random password into a `Secret` (or uses `postgres.password` if you supply one). Subsequent `helm upgrade` calls reuse the existing Secret via the `lookup` function so you don't get locked out.
- **Persistent volumes are kept on uninstall.** The Postgres `Secret` carries `helm.sh/resource-policy: keep`. Delete the PVC and Secret manually for a clean reinstall.
- **CI guardrail.** Every PR that touches `charts/` is gated by [`helm-lint.yml`](https://github.com/nightBaker/fleans/blob/main/.github/workflows/helm-lint.yml), which runs `helm lint` and `helm template` against both default values and the full feature set.

## Limitations

- **SQLite is not supported in production.** SQLite is the default `persistence.provider` for local Aspire-based dev only. The chart accepts the override but you would have to wire a writable per-pod volume yourself, and Orleans clustering across silos against a per-pod SQLite file is not a supported configuration. Use Postgres for any multi-replica deployment.
- **No cosign image signing yet.** Signing of the published images is tracked in [#410](https://github.com/nightBaker/fleans/issues/410).
- **Chart not yet published to an OCI registry.** Install from the repo (`helm install fleans charts/fleans/`) or from a `helm package`-produced `.tgz` attached to the matching GitHub Release.
