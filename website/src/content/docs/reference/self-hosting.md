---
title: Self-hosting
description: Where the install guides live — Docker Compose, the Helm chart, running the images yourself — and the NuGet packages for custom-task workers.
sidebar:
  order: 9
---

Fleans runs from released container images. Pick the guide for where you run it:

- [Self-host with the Helm chart](/fleans/guides/self-host-helm/) — Kubernetes. The chart is
  published as a GitHub Release asset (`fleans-<version>.tgz`); install it with
  `helm install fleans ./fleans-<version>.tgz`. The guide lists what the chart deploys, its
  values, the production checklist and the security defaults.
- [Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/) — the release's
  Compose bundle on one host.
- [Deployment → Running the containers yourself](/fleans/reference/deployment/#running-the-containers-yourself)
  — ports, roles and the minimum settings per process, for ECS, Nomad, VMs or plain Docker.
- [Simplest start: one PostgreSQL](/fleans/guides/topology-single-postgres/) and
  [Recommended for scale](/fleans/guides/topology-scalable/) — the two reference layouts.
- [Upgrading](/fleans/guides/upgrading/) — moving to a new release before 1.0.

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
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Fleans.ExternalTasks.Sdk" Version="0.14.0" />
    <PackageReference Include="Fleans.Plugins.RestCaller" Version="0.14.0" />
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
