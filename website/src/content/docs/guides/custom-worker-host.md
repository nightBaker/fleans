---
title: Hosting Your Own Custom-Task Plugins
description: Use the fleans-custom-worker-example GitHub template to run your own custom-task plugins outside the engine repository.
---

When you ship a custom-task plugin (e.g. an in-house "send-slack-notification" plugin), you
typically want it to run in **your own deployable** — not inside the Fleans engine image.

The **[`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)**
repository is a GitHub template that shows the minimum-viable shape for that deployable:
a Web-SDK Exe that boots an Orleans silo with `Fleans:Role=Worker`, references **only**
`Fleans.Worker` and the chosen plugin assemblies via NuGet, and joins the same Orleans
cluster as the engine via Redis clustering.

## Quick start

Click **Use this template** on the [example repo](https://github.com/nightBaker/fleans-custom-worker-example),
or via gh CLI:

```bash
gh repo create my-plugin-host --template nightBaker/fleans-custom-worker-example --public
cd my-plugin-host
ConnectionStrings__orleans-redis="localhost:6379" dotnet run --project src/Fleans.CustomWorkerHost
```

That gives you a working silo claiming the `rest-call` handler from the template's pre-registered
[`Fleans.Plugins.RestCaller`](https://github.com/nightBaker/fleans-plugins/blob/main/docs/rest-caller/README.md) package.
Swap in your own plugin (see [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/))
and ship the resulting container image alongside the engine.

## Why a separate host?

- **Operational isolation** — you can deploy, scale, and roll back your plugin worker
  independently from the engine. Engine releases don't force you to rebuild the plugin
  worker; plugin source changes don't force you to redeploy the engine.
- **Reference hygiene** — the example template depends only on `Fleans.Worker` (a leaf
  NuGet) and the plugin packages. **No `Fleans.Application` / `Fleans.Domain` /
  `Fleans.Infrastructure` / persistence references** — structurally guaranteed by a CI
  leaf-package guard in the template repo. This means your plugin host cannot accidentally
  execute engine grains.
- **Independent release cadence** — your plugin host repo lives outside the engine repo, so
  you bump it on your own schedule and contributors don't need engine commit access.

## Configure the shared reminder service

Your plugin host joins the engine's Orleans cluster, so it owns part of the cluster's
reminder ring and serves some of the engine's BPMN-timer reminders. Configure it with the
**same reminder provider as the engine** — by default the Redis reminder table on the
`orleans-redis` connection (`Microsoft.Orleans.Reminders.Redis`,
`siloBuilder.UseRedisReminderService(...)`). Without a reminder service, timers whose range
lands on your host fail to register and their activities fail; with an in-memory one
(`UseInMemoryReminderService()`), those timers are lost whenever your host restarts.

If the engine runs `Fleans__Reminders__Provider=Postgres` (recommended in production), add the
`Microsoft.Orleans.Reminders.AdoNet` and `Npgsql` packages and point your host at the same
database as the engine — `ConnectionStrings__fleans-reminders`, or `ConnectionStrings__fleans`
when the engine has no dedicated reminders database:

```csharp
siloBuilder.UseAdoNetReminderService(o =>
{
    o.Invariant = "Npgsql";
    o.ConnectionString = builder.Configuration.GetConnectionString("fleans-reminders")
        ?? builder.Configuration.GetConnectionString("fleans");
});
```

The engine silos create the reminders tables; your host only reads and writes them.

## Join the engine's membership table

Your host must use the **same Orleans membership table as the engine** (`Fleans__Clustering__Provider`);
on another table it forms a cluster of its own and never receives a task. The default is the Redis
table on `orleans-redis` (`Microsoft.Orleans.Clustering.Redis`, `siloBuilder.UseRedisClustering(...)`).
If the engine runs `Fleans__Clustering__Provider=Postgres`, add `Microsoft.Orleans.Clustering.AdoNet`
and point your host at the engine's membership database:

```csharp
siloBuilder.UseAdoNetClustering(o =>
{
    o.Invariant = "Npgsql";
    o.ConnectionString = builder.Configuration.GetConnectionString("fleans-cluster")
        ?? builder.Configuration.GetConnectionString("fleans");
});
```

The engine silos create the membership tables. Set `Orleans__ServiceId` / `Orleans__ClusterId` to
the engine's values. If you also enable Orleans Kubernetes hosting (`siloBuilder.UseKubernetesHosting()`,
package `Microsoft.Orleans.Hosting.Kubernetes`, called *after* `AddFleansPluginHost` so its silo name
wins), the pod name becomes the silo name — name your
Deployment `plugin-…` so the silo keeps the `plugin-` prefix Fleans placement relies on, label the
pods `orleans/serviceId` / `orleans/clusterId`, and grant the pod's ServiceAccount `get`, `list`,
`watch` and `patch` on `pods`.

## Plugin-author NuGet stack

Three NuGet packages compose the plugin-author surface, layered strictly:

<figure class="arch-diagram" style="--arch-ratio: 1260 / 470">
  <iframe data-arch-src="/fleans/diagrams/plugin-nuget-stack.html" src="/fleans/diagrams/plugin-nuget-stack.html?embed=1" title="Plugin-author NuGet package stack" loading="lazy"></iframe>
  <figcaption>Your plugin depends on <code>Fleans.Worker</code> (which pulls in the abstraction packages); a host built from the template references it plus your plugins and joins the engine's Orleans cluster. <a href="/fleans/diagrams/plugin-nuget-stack.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

```
Fleans.Worker  →  Fleans.Application.Abstractions  →  Fleans.Domain.Abstractions
```

| Package | Holds |
|---------|-------|
| `Fleans.Domain.Abstractions` | `IDomainEvent`, `ExecuteCustomTaskEvent`, `InputMapping`/`OutputMapping`, `CustomTaskFailedActivityException` + exception hierarchy. True leaf — depends only on `Microsoft.Orleans.Sdk`. |
| `Fleans.Application.Abstractions` | Grain interfaces (`IScriptExecutorGrain`, `IConditionExpressionEvaluatorGrain`, `ICustomTaskCatalogGrain`, narrow `IWorkflowInstanceCallback`), `CustomTaskParameterSchema`, `WorkflowEventStreams`, `WorkflowLoggingContext`. |
| `Fleans.Worker` | `CustomTaskHandlerBase`, `[WorkerPlacement]`, placement directors. |

Your plugin host references `Fleans.Worker` + each plugin package; the rest comes in
transitively. Verify the closure with:

```bash
dotnet list package --include-transitive --project src/Fleans.CustomWorkerHost | grep -i fleans
```

You should see only `Fleans.*.Abstractions`, `Fleans.Worker`, and your plugin packages — no
`Fleans.Application` or `Fleans.Domain` (without the `.Abstractions` suffix).

## Adding a new plugin

1. Author your plugin grain by deriving from `CustomTaskHandlerBase` (see
   [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/)).
2. Publish your plugin as a NuGet package, or `<ProjectReference>` if it lives in your
   monorepo.
3. Add the reference to your `CustomWorkerHost` csproj.
4. Add `services.AddYourPlugin();` to the registration block in `Program.cs`.
5. Rebuild and redeploy the worker container.

## Plugin SemVer caveat

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical (same precedent
> as `Aspire.Hosting.*` / `Microsoft.Orleans.*`). Pin to a major+minor that matches the
> engine you're deploying alongside.
>
> Upgrade the host together with the engine. A plugin host reads a share of the engine's
> stream queues and must deserialize every event on them, not just its own custom-task events.
> If an engine release adds a stream event type, a host on an older `Fleans.Worker` cannot read
> it. On Kafka that event is then dropped (or dead-lettered) and the workflow instance waiting
> for it hangs.

## Cross-references

- [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) — the GitHub template repo.
- [Custom Tasks concept page](/fleans/concepts/custom-tasks/) — the BPMN-side picture.
- [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/) — handler authoring.
