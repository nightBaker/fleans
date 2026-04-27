# Fleans Helm chart

Deploy the [Fleans](https://nightbaker.github.io/fleans/) BPMN workflow engine on Kubernetes.

## What this chart deploys

| Component | Image | Purpose |
| --------- | ----- | ------- |
| `core` Deployment | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Core`). Hosts coordinator grains. |
| `worker` Deployment (optional) | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Worker`). Hosts `[StatelessWorker]` script/condition grains. Same image as `core` — different role env var. |
| `web` Deployment (optional, default on) | `ghcr.io/nightbaker/fleans-web:<tag>` | Blazor Server admin UI. |
| `mcp` Deployment (optional, default on) | `ghcr.io/nightbaker/fleans-mcp:<tag>` | MCP server (port 5200) for AI-agent integration. |
| `redis` StatefulSet | `redis:7-alpine` | Orleans clustering + grain storage. Required. |
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

### OIDC for the admin UI

```yaml
auth:
  authority: https://idp.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc   # Secret with key `client-secret`
```

Leave `auth.authority` empty to ship without authentication (single-tenant / dev).

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

## Upgrade notes

- **Postgres password is sticky.** First install generates a random password into a `Secret` (or uses `postgres.password` if you supply one). Subsequent `helm upgrade` calls reuse the existing Secret via the `lookup` function so you don't get locked out.
- **Persistent volumes are kept on uninstall.** The Postgres Secret carries `helm.sh/resource-policy: keep`. Delete the PVC and Secret manually for a clean reinstall.

## Verifying changes

```bash
helm lint charts/fleans/
helm template fleans charts/fleans/ > /tmp/rendered.yaml
diff -u <(echo "previous-rendered") /tmp/rendered.yaml | head -100   # eyeball diffs in PRs
```

## Acceptance reference (from #407)

| Criterion | Where to check |
| --------- | -------------- |
| Chart skeleton at `charts/fleans/` with `Chart.yaml`, `values.yaml`, `templates/` | this directory |
| Lints clean | `helm lint charts/fleans/` |
| Installs against kind | "Quick install — kind" section above |
| Image tag defaults to `appVersion` | `image.tag` default in `values.yaml`, fallback in `templates/_helpers.tpl::fleans.imageTag` |
| No secrets in repo | OIDC client-secret + Postgres password go through `Secret` (`existingSecret` or chart-managed); `values.yaml` defaults to placeholders |
