# Cluster roles & grain placement

## The `Fleans:Role` config knob

`Fleans.Api` reads `Fleans:Role` at startup. Values: `Core`, `Worker`, `Combined` (case-insensitive, default `Combined`; invalid values throw). The role is stamped into `SiloOptions.SiloName` as `{role}-{machine}-{guid}` so other silos see it via Orleans membership.

`Fleans.Worker` hosts the `[StatelessWorker]` script/condition grain **implementations** (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`); their interfaces remain in `Fleans.Application` so callers don't need a Worker reference.

**When adding a new worker-type grain**, put the implementation in `Fleans.Worker` and keep the interface next to the caller in `Fleans.Application`. The split is structural only (separate assembly, separate role config) — there is no runtime placement filtering, so a `Core`-tagged silo will still host a worker grain if one is needed there.

Aspire stamps `Fleans__Role` on every project: dev mode tags `fleans-core` as `Combined` (the 3-process topology must host worker grains in-process); publish mode tags it `Core` to match `deployment-core.yaml` in the Helm chart. Set `FLEANS_ROLE=<value>` in the Aspire host's environment to override either default — useful for testing the Core/Worker split locally without editing source.

## `Fleans.WorkerHost`

The dedicated deployable for the Worker role — a thin Web SDK Exe that:

- boots an Orleans silo with `Fleans:Role=Worker` by default,
- references the `Fleans.Worker` class library for grain implementations + placement directors,
- wires the same persistence/streaming/Redis stack as `Fleans.Api`.

It is registered with Aspire **only in publish mode** (`builder.ExecutionContext.IsPublishMode`), so `dotnet run --project Fleans.Aspire` keeps the original 3-process dev topology and `aspire publish -t kubernetes` / `-t docker-compose` emits a fourth `fleans-worker` deployment alongside `fleans-core` (Api), `fleans-management` (Web), and `fleans-mcp` (Mcp).

Container image name: `fleans-worker` via `<ContainerRepository>` in `Fleans.WorkerHost.csproj`.

## Three-role placement contract

`Fleans:Role` accepts three values cluster-wide:

- **`Core`** (silo prefix `core-`) — engine `Fleans.Api` / `Fleans.Web` / `Fleans.Mcp`; hosts `[CorePlacement]` grains.
- **`Worker` / `Combined`** (silo prefix `worker-` / `combined-`) — engine workers (`Fleans.WorkerHost` in publish mode, `Fleans.Api` in Combined dev mode); host `[WorkerPlacement]` grains (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`) and any engine-bundled plugins.
- **`Plugin`** (silo prefix `plugin-`) — **external** custom worker hosts that host ONLY the plugin grains registered via `AddCustomTaskPlugin<T>()`. Engine-internal `[WorkerPlacement]` grains never land here because `WorkerPlacementDirector.HasWorkerRole` rejects the `plugin-` prefix.

`CustomTaskHandlerBase` subclasses MUST NOT carry `[WorkerPlacement]` — they use Orleans default placement, which relies on `GetCompatibleSilos` to pick a silo that has the concrete handler assembly loaded. Per-plugin isolation falls out of grain-class discovery; do not add a custom placement attribute or director for plugin handlers without re-evaluating this invariant.

External plugin hosts call `siloBuilder.AddFleansPluginHost(configuration)` (from `Fleans.Worker.Hosting`) which validates the role config and stamps the `plugin-` silo name. See the template repo at <https://github.com/nightBaker/fleans-custom-worker-example>.

## Placement-types package boundary

`Core*` placement types (`CorePlacementStrategy`, `CorePlacementDirector`, `CorePlacementAttribute`) live in `Fleans.Application.Abstractions/Placement/` (namespace `Fleans.Application.Placement`) so external plugin hosts can register them — `AddFleansPluginHost` in `Fleans.Worker` calls `AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>()` to route `[CorePlacement]` grain activations (e.g. `CustomTaskCatalogGrain`) from a plugin silo back to a `core-`/`combined-` engine silo.

`Worker*` placement types (`WorkerPlacementStrategy`, `WorkerPlacementDirector`, `WorkerPlacementAttribute`) live in `Fleans.Worker.Placement` — they do NOT need to be in Abstractions today because `AddFleansPluginHost` is itself in `Fleans.Worker`, and the engine project refs (`Fleans.Api`, `Fleans.WorkerHost`) already pull `Fleans.Worker` so their own `AddPlacementDirector<WorkerPlacementStrategy, …>()` calls compile.

**If a future engine grain inside `Fleans.Application` is annotated with `[WorkerPlacement]`, the entire `Worker*` placement triplet must also be promoted to `Fleans.Application.Abstractions/Placement/` with namespace preserved (`Fleans.Worker.Placement` → mirror Core* layout)** — the current asymmetry is contingent on `grep -rE '\[WorkerPlacement\]' src/Fleans/Fleans.Application` returning empty, and breaks the moment that contract changes.

The regression test `PluginHostPlacementTests.AddFleansPluginHost_RegistersBothPlacementDirectors` guards that both directors stay wired in `AddFleansPluginHost`; any new placement strategy added to plugin-host registration MUST extend that test.

## Custom-worker host template (external repo)

The "host your own custom-task plugins" worked example lives at **<https://github.com/nightBaker/fleans-custom-worker-example>**, marked as a GitHub template (`Use this template` button).

It depends only on `Fleans.Worker` + plugin packages from nuget.org — no `Fleans.Application` / `Fleans.Domain` / `Fleans.Infrastructure` / persistence references (structurally enforced by a CI leaf-package guard). The example was extracted from this repo in v0.2.0+ once the leaf-package refactor made `Fleans.Worker` consumable from outside the engine source tree.

Plugin authors:

1. Click "Use this template" to scaffold their own host.
2. Set `Fleans:Role=Plugin` (silo prefix `plugin-`).
3. Call `siloBuilder.AddFleansPluginHost(builder.Configuration)` from `Fleans.Worker.Hosting` in their `Program.cs` — this validates the role and stamps the `plugin-<machine>-<guid>` silo name.
4. Register plugins via `services.AddXxxPlugin()`.
5. Ship the resulting container image alongside the engine.

This repo's release pipeline does NOT publish a `fleans-custom-worker` image — the image matrix is `api/web/worker/mcp` only.
