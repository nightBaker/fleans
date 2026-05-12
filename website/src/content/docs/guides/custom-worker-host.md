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

That gives you a working silo claiming the bundled `Fleans.Plugins.RestCaller` handler.
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

## Plugin-author NuGet stack

Three NuGet packages compose the plugin-author surface, layered strictly:

```
Fleans.Worker  →  Fleans.Application.Abstractions  →  Fleans.Domain.Abstractions
```

| Package | Holds |
|---------|-------|
| `Fleans.Domain.Abstractions` | `IDomainEvent`, `ExecuteCustomTaskEvent`, `InputMapping`/`OutputMapping`, `CustomTaskFailedActivityException` + exception hierarchy. True leaf — depends only on `Microsoft.Orleans.Sdk`. |
| `Fleans.Application.Abstractions` | Grain interfaces (`IScriptExecutorGrain`, `IConditionExpressionEvaluatorGrain`, `ICustomTaskCatalogGrain`, narrow `IWorkflowInstanceCallback`), `CustomTaskParameterSchema`, `MappingResolver`, `WorkflowEventStreams`, `WorkflowLoggingContext`. |
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

## Cross-references

- [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) — the GitHub template repo.
- [Custom Tasks concept page](/fleans/concepts/custom-tasks/) — the BPMN-side picture.
- [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/) — handler authoring.
