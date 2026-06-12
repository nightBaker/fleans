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

## Plugin packages on NuGet

Fleans publishes four plugin-author packages to nuget.org on every tagged GitHub Release. They are the supported way to write a custom-task plugin or stand up your own plugin host without depending on the Fleans repo as a Git submodule. The packages are layered strictly so plugin authors get only what they need:

```
Fleans.Worker  →  Fleans.Application.Abstractions  →  Fleans.Domain.Abstractions
```

| Package | When to use it |
| --- | --- |
| [`Fleans.Domain.Abstractions`](https://www.nuget.org/packages/Fleans.Domain.Abstractions/) | True leaf — depends only on `Microsoft.Orleans.Sdk`. Holds `IDomainEvent`, `ExecuteCustomTaskEvent`, `InputMapping`/`OutputMapping`, `CustomTaskFailedActivityException` + the exception hierarchy. Pulled in transitively by `Fleans.Application.Abstractions`; rarely referenced directly. |
| [`Fleans.Application.Abstractions`](https://www.nuget.org/packages/Fleans.Application.Abstractions/) | Grain interfaces (script/condition/custom-task/narrow `IWorkflowInstanceCallback`), schema records, mapping resolver, stream constants. Pulled in transitively by `Fleans.Worker`; rarely referenced directly. |
| [`Fleans.Worker`](https://www.nuget.org/packages/Fleans.Worker/) | Worker-side primitives: `CustomTaskHandlerBase`, `[WorkerPlacement]`, the placement directors. Reference this from any project that defines a custom-task plugin. |
| [`Fleans.Plugins.RestCaller`](https://www.nuget.org/packages/Fleans.Plugins.RestCaller/) | Worked-example plugin — the `<serviceTask type="rest-call">` HTTP caller. Useful as a copy-template for new plugins, or to register the REST caller directly in your own worker host. |

### Starter template

The **[`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)** GitHub template is the supported scaffolding for "host your own custom-task plugins". Click *Use this template* to start your own plugin host repo — see the [custom-worker-host guide](/fleans/guides/custom-worker-host/) for details.

All three packages share the engine's `<VersionPrefix>` track — every Fleans release bumps every plugin's NuGet version even when the plugin source is bit-identical (same precedent as `Aspire.Hosting.*` and `Microsoft.Orleans.*`). Pin to the same version across the three when you upgrade.

### Why we publish to NuGet

A plugin is just a class that derives from `Fleans.Worker.CustomTasks.CustomTaskHandlerBase`. Pre-NuGet, plugin authors had to either fork the engine repo or vendor `Fleans.Worker.csproj` as a submodule — both forced a coupling between plugin code and the engine repo's branch layout. Publishing the three plugin packages to nuget.org makes the plugin host a normal .NET project: `dotnet new console`, `dotnet add package Fleans.Worker`, write your handler, ship.

The packages include SourceLink + `.snupkg` symbols, so debugging into the engine's worker primitives Just Works in Visual Studio / Rider with "Enable Source Link" + "Enable source server support" turned on.

### How to consume them

The minimum plugin-host project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Pin all three to the same Fleans engine version. -->
    <PackageReference Include="Fleans.Worker" Version="0.1.0-beta" />
    <PackageReference Include="Fleans.Plugins.RestCaller" Version="0.1.0-beta" />
  </ItemGroup>
</Project>
```

A minimal `Program.cs` for a custom worker host:

```csharp
using Fleans.Plugins.RestCaller;        // ships the rest-call handler

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(silo =>
{
    silo.UseRedisClustering(opts => opts.ConfigurationOptions = ...);
    silo.AddRedisGrainStorageAsDefault(opts => opts.ConfigurationOptions = ...);
    silo.Configure<SiloOptions>(o => o.SiloName = $"plugin-host-{Environment.MachineName}");
});

// Register every plugin you ship in this host.
builder.Services.AddRestCallerPlugin();

await builder.Build().RunAsync();
```

When the silo joins the Orleans cluster, `<bpmn:serviceTask type="rest-call">` activities are automatically claimed by your plugin handler. See [Custom Tasks](../../concepts/custom-tasks/) for the handler authoring guide and the [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) GitHub template for a complete worked example.

### Release cadence

The packages are published by [`.github/workflows/nuget-publish.yml`](https://github.com/nightBaker/fleans/blob/main/.github/workflows/nuget-publish.yml) on `release.published`. The workflow can also be invoked manually with `workflow_dispatch` and `version=0.0.0-ci-test` to dry-run pack + upload-artifact without touching nuget.org. Re-runs against an already-published version are no-ops via `dotnet nuget push --skip-duplicate`.

### Package integrity & signatures

Every Fleans plugin package you install from nuget.org carries nuget.org's **repository signature**. nuget.org applies this signature **server-side, when it accepts the upload** — it is not produced by the Fleans build, and it is present on the package you restore, not on a locally-packed `.nupkg`. The repository signature chains to a root that .NET trusts by default, so you can verify any restored Fleans package:

```bash
# Resolve the package, then verify the signature on the restored .nupkg in your global packages cache.
dotnet add package Fleans.Worker
dotnet nuget verify --all \
  ~/.nuget/packages/fleans.worker/<version>/fleans.worker.<version>.nupkg
```

`verify --all` exits `0` and reports a valid **repository** signature; it fails (`NU3004`) if the package is unsigned or has been tampered with after nuget.org signed it.

**What this guarantees, and what it does not.** The repository signature gives you **integrity** — the package is exactly what nuget.org accepted and has not been altered in transit or on the feed. It does **not** assert *publisher* identity: it proves nuget.org holds the package, not that the Fleans project specifically signed it. For a pre-1.0 project this is a deliberate trade-off — repository signing delivers the tamper-resistance most consumers need at zero setup, and **publisher (author) signing** is a planned follow-up ([#728](https://github.com/nightBaker/fleans/issues/728)) for when publisher-authenticated signatures become worth the certificate onboarding. Until then, no project-managed signing certificate or per-consumer trust step is required.

## Limitations

- **SQLite is not supported in production.** SQLite is the default `persistence.provider` for local Aspire-based dev only. The chart accepts the override but you would have to wire a writable per-pod volume yourself, and Orleans clustering across silos against a per-pod SQLite file is not a supported configuration. Use Postgres for any multi-replica deployment.
- **No cosign image signing yet.** Signing of the published images is tracked in [#410](https://github.com/nightBaker/fleans/issues/410).
- **Chart not yet published to an OCI registry.** Install from the repo (`helm install fleans charts/fleans/`) or from a `helm package`-produced `.tgz` attached to the matching GitHub Release.

## See also

- [Observability](/fleans/reference/observability/) — health checks, metrics, logging, tracing, dashboards, alerting
