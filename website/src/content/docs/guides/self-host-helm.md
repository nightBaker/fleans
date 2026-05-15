---
title: Self-host with the Helm chart
description: Install Fleans on Kubernetes from a release-asset Helm chart tarball. Walks through download, install, values reference, production checklist, upgrade, and troubleshooting.
---

The Fleans release pipeline publishes a packaged Helm chart (`fleans-<VER>.tgz`)
on every `v<SemVer>` tag. This guide walks through the install lifecycle
against a real Kubernetes cluster: acquire the tarball, install with sane
defaults, harden for production, upgrade across releases, and recover from
common failures. For chart internals (component matrix, kind smoke test,
chart structure), the canonical reference is
[Self-Hosting on Kubernetes](/fleans/reference/self-hosting/).

## Prerequisites

- **Kubernetes 1.27+** (the chart is built and tested against the current
  three minor versions).
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
gh release download v0.1.0-beta --repo nightBaker/fleans -p 'fleans-*.tgz'
```

Or via curl:

```bash
curl -LO https://github.com/nightBaker/fleans/releases/download/v0.1.0-beta/fleans-0.1.0-beta.tgz
```

:::note[OCI publishing is deferred]
Publishing the chart to an OCI registry (`oci://ghcr.io/nightbaker/charts/fleans`)
is tracked in [#410](https://github.com/nightBaker/fleans/issues/410). Until
then, the GitHub-Release-asset path is the supported install vector.
:::

## Verify the chart and images

The release pipeline signs both the chart tarball (as a blob) and every container image (by manifest digest). Run two checks before `helm install`:

**1. Verify the helm chart tarball.** Download `fleans-0.1.0-beta.tgz`, `fleans-0.1.0-beta.tgz.sig`, and `fleans-0.1.0-beta.tgz.crt` from the same GitHub Release, then:

```bash
cosign verify-blob \
  --certificate fleans-0.1.0-beta.tgz.crt \
  --signature   fleans-0.1.0-beta.tgz.sig \
  --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  fleans-0.1.0-beta.tgz
```

**2. Verify each container image.** The chart pulls four images (one of which — `fleans-api` — is reused by the `core` and `worker` Deployments per the chart's `values.yaml` design; even though the Helm chart pulls only `image.api` for both `core` and `worker` Deployments at runtime, the release pipeline publishes all four as distinct signed images for users who want a Worker silo deployable in non-chart deployments):

```bash
for SVC in api web worker mcp; do
  cosign verify \
    --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
    --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    ghcr.io/nightbaker/fleans-$SVC:0.1.0-beta
done
```

For production Kubernetes installs, the recommended enforcement is the Sigstore Policy Controller (`policy.sigstore.dev/v1beta1 ClusterImagePolicy`) or Kyverno's `verifyImages`/`verifyManifests` rules.

## 2. Quick install

```bash
helm install fleans ./fleans-0.1.0-beta.tgz \
  --namespace fleans \
  --create-namespace

kubectl wait --for=condition=ready pod \
  -l app.kubernetes.io/instance=fleans \
  --timeout=180s --namespace fleans

kubectl port-forward -n fleans svc/fleans-web 8080:8080
```

Open [http://localhost:8080](http://localhost:8080) — the admin UI loads.
Tear down at any time with `helm uninstall fleans -n fleans`.

## 3. `values.yaml` reference

The defaults below are extracted from `charts/fleans/values.yaml`. Override
on the install command line via `--set` or pass a `-f my-values.yaml` overlay.

| Key | Default | Description |
| --- | --- | --- |
| `image.api.repository` | `ghcr.io/nightbaker/fleans-api` | Image used by both `core` and `worker` Deployments. |
| `image.web.repository` | `ghcr.io/nightbaker/fleans-web` | Blazor Server admin UI image. |
| `image.mcp.repository` | `ghcr.io/nightbaker/fleans-mcp` | MCP server image. |
| `image.tag` | *(empty — falls back to chart `appVersion`)* | Pin to a specific tag for reproducible installs. |
| `image.pullPolicy` | `IfNotPresent` | Standard Kubernetes pull policy. |
| `imagePullSecrets` | `[]` | Add `[{name: my-pull-secret}]` when mirroring images. |
| `core.replicas` | `1` | Number of Core silos. |
| `core.resources.requests` | `cpu: 250m, memory: 512Mi` | Right-sized for ~10 active workflows; bump for higher throughput. |
| `core.resources.limits.memory` | `1Gi` | Hard cap. |
| `worker.enabled` | `false` | Set `true` to dedicate pods to `[StatelessWorker]` script/condition grains. |
| `worker.replicas` | `1` | Worker silo count when `worker.enabled=true`. |
| `worker.nodeSelector` / `worker.tolerations` | `{}` / `[]` | Pin worker silos to spot/cheaper nodes. |
| `customWorker.enabled` | `false` | Set `true` to host user-written custom-task plugins on a dedicated silo built from the [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) template. |
| `customWorker.replicas` | `1` | Custom-worker silo count when `customWorker.enabled=true`. |
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
| `postgres.persistentVolume.enabled` | `true` | Disable for ephemeral data; persistence requires a `StorageClass`. |
| `postgres.persistentVolume.size` | `8Gi` | Size the PVC. |
| `redis.enabled` | `true` | Required — Orleans clustering depends on Redis. |
| `redis.image.tag` | `"7-alpine"` | Pinned. |
| `redis.persistentVolume.enabled` | `false` | Off by default; Orleans state is rebuilt from Postgres event store. |
| `auth.authority` | `""` | OIDC issuer URL. Leaving empty disables auth (Fleans.Web fallback). |
| `auth.clientId` | `""` | Web admin UI OIDC client ID. |
| `auth.clientSecretExistingSecret` | `""` | Reference a `Secret` with key `client-secret`. **Use this in production.** |
| `auth.clientSecret` | `""` | Inline OIDC client secret. **Not recommended for production.** |
| `streaming.provider` | `Memory` | `Memory` (in-process) or `Kafka` (durable). |
| `streaming.kafka.brokers` | `""` | Comma-separated Kafka brokers, e.g. `kafka.kafka.svc:9092`. |
| `extraEnv` | `[]` | Extra env vars on every Fleans workload (`api`, `web`, `mcp`). Use for `ConnectionStrings__fleans` overrides. |

Cross-link: see [the chart component matrix](/fleans/reference/self-hosting/#what-the-chart-deploys)
for the per-Deployment image / port table — not duplicated here.

## 4. Production checklist

Each item below maps to a `values.yaml` override. Combine into a single
`values-prod.yaml` overlay and pass with `-f`.

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
  --from-literal=connection-string='Host=my-pg.example.com;Port=5432;Database=fleans;Username=fleans;Password=...;Ssl Mode=Require'
```

The Postgres user needs `CREATE TABLE` on first boot (for migrations), then
only `INSERT/UPDATE/SELECT/DELETE` thereafter.

### OIDC for the admin UI

```yaml
auth:
  authority: https://idp.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc   # Secret with key `client-secret`
```

The chart wires the Web pods' env from this `Secret`. See
[Authentication](/fleans/reference/authentication/) for the OIDC scopes
the admin UI requests and the API's JWT validation contract.

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

For compute-isolation of script / condition grains:

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
`core.replicas` and bump CPU/memory based on observed throughput in the
Orleans Dashboard. See [Observability](/fleans/reference/observability/) for
the dashboard URL and metrics that matter.

### Image-pull secrets (private registry mirroring)

```yaml
imagePullSecrets:
  - name: my-registry-pull
```

## 5. Upgrade path

```bash
gh release download v0.2.0 --repo nightBaker/fleans -p 'fleans-*.tgz'
helm upgrade fleans ./fleans-0.2.0.tgz \
  --namespace fleans \
  -f values-prod.yaml
```

Helm rolls the new pods forward; `Fleans.Api` runs pending EF migrations on
startup against the existing Postgres state. If the rollout misbehaves:

```bash
helm rollback fleans <REVISION> -n fleans
```

`helm history fleans -n fleans` lists revision numbers. Rollback is
non-destructive to Postgres; the database schema is forward-compatible
within the documented per-release migration window.

### 5.1. Draining workflows for stream-format-changing upgrades

Releases flagged as **"stream upgrade"** in the release notes change Orleans
stream identity — messages on the old stream key become orphaned after the
upgrade, so in-flight workflows must drain first. Procedure (no engine
changes required, works for any Redis-streaming adapter version):

```bash
# 1. Stop API/Web/Mcp so no new work arrives. Workers keep running and
#    continue to consume queued events.
kubectl -n fleans scale deployment/fleans-api --replicas=0
kubectl -n fleans scale deployment/fleans-web --replicas=0
kubectl -n fleans scale deployment/fleans-mcp --replicas=0

# 2. Wait for Redis streams to drain. The redis resource ships as a
#    StatefulSet in the chart — confirm the actual name and key pattern
#    against your cluster:
kubectl -n fleans get statefulset
kubectl -n fleans exec statefulset/fleans-redis -- redis-cli KEYS '*' | head

# Then poll stream lengths until all hit 0:
while true; do
  L=$(kubectl -n fleans exec statefulset/fleans-redis -- sh -c \
        "redis-cli --scan --pattern 'fleans/StreamProvider/*' \
         | xargs -L 1 redis-cli XLEN | sort -u")
  echo "stream-lengths=$L"
  [ "$L" = "0" ] && break
  sleep 10
done

# 3. Apply the upgrade.
helm upgrade fleans ./fleans-<ver>.tgz -n fleans -f values-prod.yaml

# 4. Scale API/Web/Mcp back up (or rely on the chart's defaults).
kubectl -n fleans scale deployment/fleans-api --replicas=1
kubectl -n fleans scale deployment/fleans-web --replicas=1
kubectl -n fleans scale deployment/fleans-mcp --replicas=1
```

The redis StatefulSet name (`fleans-redis` above) depends on the Helm release
name and the chart's naming template. If you renamed the release or use a
chart fork, substitute the name `kubectl get statefulset` reports.

## 6. Troubleshooting

- **`ImagePullBackOff`.** Either the registry is private and `imagePullSecrets`
  is missing, or the tag doesn't exist. `kubectl describe pod <name>`
  shows the underlying error.
- **`CrashLoopBackOff` on `fleans-api` with Postgres connection error.**
  When `postgres.enabled=false`, the `extraEnv` `ConnectionStrings__fleans`
  must resolve before the API pod starts. Check the referenced `Secret` via
  `kubectl get secret fleans-pg -o jsonpath='{.data.connection-string}' | base64 -d`.
- **OIDC `redirect_uri_mismatch` from the IdP.** Add the new ingress host
  (e.g. `https://fleans.example.com/signin-oidc`) to the IdP's allowed
  redirect URIs.
- **Live-edit drift after `kubectl edit`.** Helm tracks the rendered manifest;
  in-cluster edits get reverted on the next `helm upgrade`. Inspect
  effective values with `helm get values fleans -n fleans`.
- **HPA fights Orleans placement.** Orleans grains stick to silos; an HPA
  scaling Core or Worker silos in/out can churn placement. Set
  `core.replicas` / `worker.replicas` explicitly for stable workloads.

## See also

- [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) — chart
  component matrix, `kind` smoke test, chart-internal architecture.
- [Configuration](/fleans/reference/configuration/) — full configuration
  matrix (env-var → `values.yaml` mapping).
- [Persistence](/fleans/reference/persistence/) — Postgres + Sqlite providers,
  external-Postgres connection-string contract.
- [Authentication](/fleans/reference/authentication/) — OIDC scopes, JWT
  validation, the cookie + bearer flows.
- [Streaming](/fleans/reference/streaming/) — Kafka provider configuration
  and production-readiness caveat.
- [Cutting a Release](https://github.com/nightBaker/fleans/blob/main/CLAUDE.md#cutting-a-release) —
  maintainer runbook for publishing a new chart tarball.
