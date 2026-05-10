---
title: Hosting Your Own Custom-Task Plugins
description: Use Fleans.CustomWorkerHost as a starting template to run your own custom-task plugins outside the engine repository.
---

When you ship a custom-task plugin (e.g. an in-house "send-slack-notification" plugin), you
typically want it to run in **your own deployable** — not inside the Fleans engine image.
`Fleans.CustomWorkerHost` is the worked example that shows the minimum-viable shape for
that deployable: a Web-SDK Exe that boots an Orleans silo with `Fleans:Role=Worker`,
references **only** `Fleans.Worker` and the chosen plugin assemblies, and joins the same
Orleans cluster as the engine via Redis clustering.

## Why a separate host?

- **Operational isolation** — you can deploy, scale, and roll back your plugin worker
  independently from the engine. Engine releases don't force you to rebuild the plugin
  worker; plugin source changes don't force you to redeploy the engine.
- **Reference hygiene** — `Fleans.CustomWorkerHost` references **only** `Fleans.Worker` and
  the plugin packages. It does not reference `Fleans.Application`, `Fleans.Domain`,
  `Fleans.Infrastructure`, or any persistence project. This is the structural guarantee
  that your plugin host doesn't accidentally execute engine grains.
- **Container co-location** — when you fork `Fleans.CustomWorkerHost` and register it in
  your fork's Aspire host, `aspire publish -t docker-compose` (or `-t kubernetes`) emits a
  separate `fleans-custom-worker` service so you can scale plugin workers independently from
  engine workers (`fleans-worker`). The Fleans engine repo's own publish output does not
  include `fleans-custom-worker` — the release pipeline only ships `api/web/worker/mcp`,
  because the in-tree project is intended as a starting template, not a default deployable.

## Comparison with Fleans.WorkerHost

| | `Fleans.WorkerHost` | `Fleans.CustomWorkerHost` |
|--|--|--|
| Purpose | Internal worker silo for the Fleans engine itself (script tasks, condition evaluators, custom-task plugins shipped in-tree) | Worked example for end users hosting their own custom-task plugins |
| References | Application + Domain + Infrastructure + Persistence (Sqlite + PostgreSQL) + Plugins.RestCaller + ServiceDefaults + Worker | **Only** Worker + chosen plugin assemblies + ServiceDefaults |
| Container image | `fleans-worker` | `fleans-custom-worker` |
| When to use | You are the engine maintainer, or you want a single deployable that bundles the engine's worker grains | You are a plugin author / operator who wants to ship plugins as a separate image |

## Minimum viable Program.cs

```csharp
using Fleans.Plugins.RestCaller;
using Fleans.ServiceDefaults;
using Fleans.Worker.Placement;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

if (string.IsNullOrEmpty(builder.Configuration["Fleans:Role"]))
{
    builder.Configuration["Fleans:Role"] = "Worker";
}

var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");
var siloName = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

    if (!string.IsNullOrEmpty(orleansRedisConnection))
    {
        siloBuilder.UseRedisClustering(orleansRedisConnection);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedisConnection));
        siloBuilder.UseInMemoryReminderService();
    }

    siloBuilder.AddFleanStreaming(builder.Configuration);
    siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();
});

// Plugin registration — operator-controlled. Add or remove .Add*Plugin() calls here to
// pick which BPMN <serviceTask type="..."> values this host claims.
builder.Services.AddRestCallerPlugin();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
```

## Sequencing — adding a new plugin

1. Author your plugin grain by deriving from `CustomTaskHandlerBase` (see
   [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/)).
2. Publish your plugin as a NuGet package (recommended) or reference the project locally.
3. Add a `<PackageReference>` (or `<ProjectReference>`) to your `CustomWorkerHost`-fork csproj.
4. Add `services.AddYourPlugin();` to the registration block in `Program.cs`.
5. Rebuild and redeploy the worker.

## Plugin SemVer caveat

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical (same precedent
> as `Aspire.Hosting.*` / `Microsoft.Orleans.*`). Pin to a major+minor that matches the
> engine you're deploying alongside.

## Cross-references

- [Custom Tasks concept page](/fleans/concepts/custom-tasks/) — the BPMN-side picture.
- [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/) — handler authoring.
- The in-tree project at `src/Fleans/Fleans.CustomWorkerHost/` is the reference
  implementation you can fork.
