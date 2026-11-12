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
| `worker` Deployment (off by default) | `ghcr.io/nightbaker/fleans-api:<tag>` | Orleans silo (`Fleans__Role=Worker`). Hosts `[StatelessWorker]` script/condition grains and the read-model projector. **Same image as `core`** — distinguished only by the `Fleans__Role` env var. |
| `web` Deployment | `ghcr.io/nightbaker/fleans-web:<tag>` | Blazor Server admin UI on port `8080`. |
| `mcp` Deployment | `ghcr.io/nightbaker/fleans-mcp:<tag>` | MCP server on port `5200` for AI-agent integration. |
| `redis` StatefulSet | `redis:7-alpine` | Orleans clustering + grain storage. **Required.** |
| `postgres` StatefulSet (default on) | `postgres:16` | Workflow persistence when `persistence.provider=Postgres`. |

Optional resources: an `Ingress` for the admin UI, a chart-managed OIDC `Secret`, and Kafka streaming environment variables on the silos.

> **Note on the Worker silo.** The worker role runs engine grains, not custom tasks, which go to [gRPC workers](/fleans/guides/custom-worker-host/). There is no `fleans-worker` image — both `core` and `worker` Deployments use `image.api`. Enabling `worker.enabled=true` lets Orleans place stateless script/condition grains on dedicated pods (and dedicated nodes via `worker.nodeSelector` / `worker.tolerations`) without pulling a second image.

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

### Authentication (OIDC for the admin UI, JWT for REST + MCP)

```yaml
auth:
  authority: https://idp.example.com/realms/fleans
  clientId: fleans-web
  clientSecretExistingSecret: fleans-oidc   # Secret with key `client-secret`
```

`auth.authority` also reaches the core, `api` and `mcp` pods, which then require JWT bearer tokens (audience `fleans-api` for REST, `fleans-mcp` for MCP). Leave `auth.authority` empty to ship without authentication (single-tenant or dev). See the [Authentication](./authentication/) reference for full configuration semantics.

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

<figure class="arch-diagram" style="--arch-ratio: 1390 / 646">
  <iframe data-arch-src="/fleans/diagrams/k8s-deployment.html" src="/fleans/diagrams/k8s-deployment.html?embed=1" title="Kubernetes production topology" loading="lazy"></iframe>
  <figcaption>Production Helm topology: TLS ingress in front of the admin UI and API, Core and Worker silos in one Orleans cluster, managed PostgreSQL and optional Kafka outside the namespace. <a href="/fleans/diagrams/k8s-deployment.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

```yaml
image:
  tag: 0.12.0            # pin — never let `latest` drift in prod

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

## Worker packages on NuGet

Fleans publishes [`Fleans.ExternalTasks.Sdk`](https://www.nuget.org/packages/Fleans.ExternalTasks.Sdk/) to nuget.org on every tagged GitHub Release. It is the supported way to write a custom-task worker in .NET: a plain generic host that connects to the API over gRPC, with no Orleans dependency and no engine source dependency. It is not needed to run the engine itself.

| Package | Holds |
|---|---|
| [`Fleans.ExternalTasks.Sdk`](https://www.nuget.org/packages/Fleans.ExternalTasks.Sdk/) | `ExternalTaskHandler`, `ExternalTaskFailedException`, `ExternalTaskParameterSchema`, `ExternalTaskVisibility`, `ExternalTaskVariables`, `AddFleansExternalTaskWorker`, `AddExternalTaskHandler<T>`, and the protocol file under `protos/`. |
| [`Fleans.Plugins.RestCaller`](https://www.nuget.org/packages/Fleans.Plugins.RestCaller/) | Not an engine package: released from [fleans-plugins](https://github.com/nightBaker/fleans-plugins) on the same version track. The `<serviceTask type="rest-call">` handler. |

`Fleans.Worker`, `Fleans.Application.Abstractions` and `Fleans.Domain.Abstractions` are no longer published.

### Starter template

The **[`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)** GitHub template is the supported scaffolding. Click *Use this template* to start your own worker repo.

### How to consume them

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Fleans.ExternalTasks.Sdk" Version="0.12.0" />
    <PackageReference Include="Fleans.Plugins.RestCaller" Version="0.12.0" />
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
using Fleans.ExternalTasks.Sdk;
using Fleans.Plugins.RestCaller;        // the rest-call handler (fleans-plugins)

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFleansExternalTaskWorker();
builder.Services.AddRestCallerPlugin();
builder.Build().Run();
```

Run it with `Fleans__ExternalTaskWorker__EngineUrl` set to the API's URL, and `<bpmn:serviceTask type="rest-call">` activities reach it. See [Custom Tasks](/fleans/concepts/custom-tasks/) for the handler authoring guide and [Hosting workers](/fleans/guides/custom-worker-host/) for production.

### Release cadence

The package is published by [`.github/workflows/nuget-publish.yml`](https://github.com/nightBaker/fleans/blob/main/.github/workflows/nuget-publish.yml) on `release.published` (plugin packages such as `Fleans.Plugins.RestCaller` are published from fleans-plugins), on the engine's version track.

### Package integrity & signatures

Every Fleans package you install from nuget.org carries nuget.org's **repository signature**. nuget.org applies this signature **server-side, when it accepts the upload** — it is not produced by the Fleans build, and it is present on the package you restore, not on a locally-packed `.nupkg`. The repository signature chains to a root that .NET trusts by default, so you can verify any restored Fleans package:

```bash
# Resolve the package, then verify the signature on the restored .nupkg in your global packages cache.
dotnet add package Fleans.ExternalTasks.Sdk
dotnet nuget verify --all \
  ~/.nuget/packages/fleans.externaltasks.sdk/<version>/fleans.externaltasks.sdk.<version>.nupkg
```

`verify --all` exits `0` and reports a valid **repository** signature; it fails (`NU3004`) if the package is unsigned or has been tampered with after nuget.org signed it.

**What this guarantees, and what it does not.** The repository signature gives you **integrity** — the package is exactly what nuget.org accepted and has not been altered in transit or on the feed. It does **not** assert *publisher* identity: it proves nuget.org holds the package, not that the Fleans project specifically signed it. For a pre-1.0 project this is a deliberate trade-off — repository signing delivers the tamper-resistance most consumers need at zero setup, and **publisher (author) signing** is a planned follow-up ([#728](https://github.com/nightBaker/fleans/issues/728)) for when publisher-authenticated signatures become worth the certificate onboarding. Until then, no project-managed signing certificate or per-consumer trust step is required.

## Limitations

- **SQLite is not supported in production.** SQLite is the default `persistence.provider` for local Aspire-based dev only. The chart accepts the override but you would have to wire a writable per-pod volume yourself, and Orleans clustering across silos against a per-pod SQLite file is not a supported configuration. Use Postgres for any multi-replica deployment.
- **No cosign image signing yet.** Signing of the published images is tracked in [#410](https://github.com/nightBaker/fleans/issues/410).
- **Chart not yet published to an OCI registry.** Install from the repo (`helm install fleans charts/fleans/`) or from a `helm package`-produced `.tgz` attached to the matching GitHub Release.

## See also

- [Observability](/fleans/reference/observability/) — health checks, metrics, logging, tracing, dashboards, alerting
