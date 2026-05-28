---
title: Hosting plugins externally
description: Run custom-task plugins in a dedicated Worker silo isolated from the engine image. Architecture, isolation guarantees, and scaffolding from the supported template.
---

## Start with the template

If you want to run your custom-task plugins in their own deployable (separate from the engine
image) — see the [Hosting Plugins (Custom Worker Host)](/fleans/guides/custom-worker-host/) guide.
The [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)
GitHub template is the supported starting point; it references **only** `Fleans.Worker`
(via NuGet) + plugin assemblies, with no `Fleans.Application` / `Fleans.Domain` in the
dependency closure.

## Overview

The recommended deployment pattern for non-trivial plugin estates is to run plugins in their
own silo process, separate from the engine's `Fleans.Api` / `Fleans.Web` / `Fleans.WorkerHost`.

## When to host externally

- **Operational isolation.** A misbehaving plugin (memory leak, runaway HTTP call) only kills
  its own silo, never the engine workers that run Script / Condition grains.
- **Independent deploy cadence.** Ship a new version of an `email` or `slack` plugin without
  rebuilding or restarting the engine.
- **Per-team ownership.** Different teams can each ship their own plugin host image; only
  `Fleans.Worker` (from nuget.org) is shared across them — no engine source dependency.

## The `Plugin` role

`Fleans:Role` accepts three values cluster-wide. The third — `Plugin` — was added for
external plugin hosts:

| Role | Silo prefix | Purpose |
|---|---|---|
| `Core` | `core-` | Engine API / Web / Mcp silos. Hosts `[CorePlacement]` grains. |
| `Worker` / `Combined` | `worker-` / `combined-` | Engine workers (and `Combined` dev silos). Hosts `[WorkerPlacement]` grains. |
| `Plugin` | `plugin-` | **External** plugin hosts. Hosts **only** plugin grains registered via `AddCustomTaskPlugin<T>()`. |

Engine binaries (`Fleans.Api`, `Fleans.WorkerHost`) **reject** `Fleans:Role=Plugin` at startup
with an explicit message — the `Plugin` role is reserved for hosts using the
`AddFleansPluginHost` helper.

## The one-liner

External hosts call a single extension method to wire up role validation, silo naming, and
default placement:

```csharp
using Fleans.Worker.Hosting;
using Fleans.Plugins.MyThing;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

var orleansRedis = builder.Configuration.GetConnectionString("orleans-redis");

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddFleansPluginHost(builder.Configuration); // role + silo name + placement director

    if (!string.IsNullOrEmpty(orleansRedis))
    {
        siloBuilder.UseRedisClustering(orleansRedis);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            opt => opt.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));
        siloBuilder.UseInMemoryReminderService();
    }

    siloBuilder.AddFleanStreaming(builder.Configuration);   // matches engine stream provider
});

builder.Services.AddMyThingPlugin();
var app = builder.Build();
app.Run();
```

`AddFleansPluginHost`:

- Validates `Fleans:Role` is `Plugin` (recommended) or `Combined`. Rejects `Core` / `Worker`
  with `InvalidOperationException`.
- Stamps the silo name as `plugin-<machine>-<guid>` so other silos see the prefix via Orleans
  membership.
- Registers `WorkerPlacementDirector` so the plugin silo participates correctly in cluster-wide
  placement decisions for `[WorkerPlacement]` grains it doesn't host.

Everything else — Redis clustering, `PubSubStore`, stream provider, `Fleans.ServiceDefaults` —
is wired separately. The minimal `Program.cs` above (`AddKeyedRedisClient`, `UseRedisClustering`,
`AddFleanStreaming`) is the complete operator-facing setup. Match the engine's Redis connection
string so plugin grains share the same Orleans cluster.

## The isolation guarantee

A plugin host runs **only** the plugin grain classes compiled into its assembly load context.
The isolation falls out of two Orleans mechanisms:

1. **Grain class discovery.** Orleans' `IPlacementContext.GetCompatibleSilos` automatically
   excludes silos that don't have the concrete grain type loaded. A `plugin-*` host that
   doesn't reference `Fleans.Plugins.RestCaller` is not a candidate for `RestCallerHandler`
   activations — Orleans never even considers it.
2. **`WorkerPlacementDirector` rejects `plugin-` prefix.** Engine-internal grains marked with
   `[WorkerPlacement]` (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`) route only
   to silos whose name starts with `worker-` or `combined-`. The `plugin-` prefix is
   explicitly excluded.

The net effect: a `plugin-*` silo carrying only an `EmailHandler` plugin grain will host
exactly that, nothing else. Engine workers continue to host Script / Condition grains plus
any engine-bundled plugins (e.g. `Fleans.Plugins.RestCaller` shipped with `Fleans.WorkerHost`).

:::note
`CustomTaskHandlerBase` subclasses **do not** carry `[WorkerPlacement]`. They use Orleans
default placement, which relies on `GetCompatibleSilos` to pick a silo that has the concrete
handler assembly loaded. Per-plugin isolation falls out of grain-class discovery; you should
not add a custom placement attribute to a plugin handler.
:::

## Scaffolding a plugin host

The [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)
repository is marked as a GitHub template. Click **Use this template** to scaffold a new
plugin-host project pre-wired with `AddFleansPluginHost`, Redis client, and a `Program.cs`
shaped for your own plugin packages. Reference your plugin NuGet packages, call their
`AddXxxPlugin()` extensions in `Program.cs`, and build the container image — the resulting
image deploys alongside the engine's `fleans-{api,web,worker,mcp}` containers.
