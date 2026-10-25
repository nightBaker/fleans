# Cluster roles & grain placement

## The `Fleans:Role` config knob

`Fleans.Api` reads `Fleans:Role` at startup. Values: `Core`, `Worker`, `Combined`, `Api` (case-insensitive, default `Combined`; invalid values throw; `Plugin` is rejected with a pointer to plugin hosts). The role is stamped into `SiloOptions.SiloName` as `{role}-{machine}-{guid}` so other silos see it via Orleans membership. With `Fleans:Kubernetes:Enabled=true` the silo name is the **pod name** instead, which must then start with `{role}-` (startup throws otherwise) — see [clustering.md](clustering.md#kubernetes-hosting-fleanskubernetesenabled).

### `Api`: REST without a silo

`Fleans:Role=Api` runs `Fleans.Api` as an **Orleans client with no grains** (`UseOrleansClient` + `clientBuilder.AddFleansClustering`, the same wiring as `Fleans.Web` / `Fleans.Mcp`), so REST ingress scales apart from the silos. Everything silo-only is skipped: silo name, reminders, stream provider, load shedding, dashboard collector, grain call filter, log-consistency provider, placement director/filter and `AddFleansPlacementAssertion`, and `AddRestCallerPlugin()` (its handler grain and silo-lifecycle registrar). What stays: controllers, auth, rate limiting, `AddApplication` / `AddInfrastructure`, persistence (the query service reads the database directly) and `EnsureDatabaseSchemaAsync`. No client stream provider is registered — no REST endpoint touches streams; add one with the silos' `Fleans:Streaming:Provider` if that changes. The Redis client follows `FleansHostKind.Client` (clustering only, see [clustering.md](clustering.md#redis-is-optional-addfleansredisclient)). The Helm chart's optional `api.*` Deployment sets it; Aspire does not (it keeps `fleans-core` a silo).

When adding a silo-only registration to `Fleans.Api/Program.cs`, put it inside the `isSilo` branch.

`Fleans.Worker` hosts the `[StatelessWorker]` script/condition grain **implementations** (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`); their interfaces live in `Fleans.Application.Abstractions` so callers don't need a Worker reference.

Who calls them: the workflow instance dispatches each script task to `ScriptExecutorGrain.Run` one-way and the executor calls back `CompleteActivity` / `FailActivity` (no stream; see [invariants](invariants.md#script-dispatch-and-in-process-conditions)). Gateway, activation, completion and conditional-event conditions do **not** go to a worker: the instance evaluates them itself with the silo's `IConditionExpressionEvaluator`, so every silo hosting `WorkflowInstance` registers it (`AddInfrastructure`). `ConditionExpressionEvaluatorGrain` is left serving conditional start events (`ConditionalStartEventListenerGrain`). Keeping scripts on worker silos is the isolation option: a `Core` + `Worker` split runs scripts off the core silos, a `Combined` silo runs them locally — one code path, no switch.

**When adding a new worker-type grain**, put the implementation in `Fleans.Worker` and keep the interface next to the caller. Mark it `[WorkerPlacement]` to keep it on `worker-`/`combined-` silos (see below).

### Stateless workers on worker silos (#857)

`ScriptExecutorGrain` and `ConditionExpressionEvaluatorGrain` carry `[StatelessWorker]` **and** `[WorkerPlacement]`. This only works because `[WorkerPlacement]` is a **placement filter** (`PlacementFilterAttribute` → `WorkerPlacementFilterStrategy` → `WorkerPlacementFilterDirector`), not a placement strategy:

- Orleans keeps **one** placement strategy per grain type — every `PlacementAttribute` writes the same `placement-strategy` grain property and the last one wins. Only `StatelessWorkerPlacement` gets the stateless-worker activator (many local activations), so a second placement attribute silently turns a stateless worker into a single-activation grain. That was #857: one script/condition activation for the whole cluster. `WorkerGrainPlacementResolutionTests` guards it (and `WorkerEvaluatorOptionsTests` the key count), including a check that no grain class carries two placement attributes.
- Placement filters run inside `GetCompatibleSilos`, *before* the director. The stateless-worker director then places locally when the caller is a `worker-`/`combined-` silo, and otherwise on a random filtered silo. With no `worker-`/`combined-` silo compatible, the filter logs EventId 11301 and keeps all compatible silos (single-silo test clusters).
- A caller silo caches the silo it picked per grain id. Callers therefore pick the key with `WorkerEvaluatorOptions.NextKey()` — a random key below `Fleans:Worker:EvaluatorKeyCount` (default 16, must be > 0) — instead of a fixed `0`, so each `core-` silo spreads its calls over several worker silos. Keep the count well above the number of worker silos; a larger value only adds idle activations. On a `worker-`/`combined-` silo every key places locally.
- Every silo that may place these grains must call `siloBuilder.AddWorkerPlacementFilter()` — Orleans throws `KeyNotFoundException` for an unregistered filter. `Fleans.Api`, `Fleans.WorkerHost` and `AddFleansPluginHost` do.

Aspire stamps `Fleans__Role` on every project: dev mode tags `fleans-core` as `Combined` (the 3-process topology must host worker grains in-process); publish mode tags it `Core` to match `deployment-core.yaml` in the Helm chart. Set `FLEANS_ROLE=<value>` in the Aspire host's environment to override either default — useful for testing the Core/Worker split locally without editing source.

## `Fleans.WorkerHost`

The dedicated deployable for the Worker role — a thin Web SDK Exe that:

- boots an Orleans silo with `Fleans:Role=Worker` by default,
- references the `Fleans.Worker` class library for grain implementations + placement directors,
- wires the same persistence/streaming/Redis/load-shedding stack as `Fleans.Api` (silo roles only — it has no `Api` role).

It is registered with Aspire **in publish mode** (`builder.ExecutionContext.IsPublishMode`), so `dotnet run --project Fleans.Aspire` keeps the original 3-process dev topology and `aspire publish -t kubernetes` / `-t docker-compose` emits a fourth `fleans-worker` deployment alongside `fleans-core` (Api), `fleans-management` (Web), and `fleans-mcp` (Mcp).

### Running the split topology locally

- `FLEANS_SPLIT_ROLES=true` (dev only) mirrors publish: `fleans-core` defaults to `Core` and `fleans-worker` is registered with `Worker` (Kestrel on a dynamic port — WorkerHost has no launch profile). `FLEANS_ROLE` still overrides the core role.
- `FLEANS_PLUGIN_HOST_PROJECT=<absolute .csproj path>` (dev only) adds a `fleans-plugin-host` resource with `Fleans__Role=Plugin`, the Orleans cluster reference and the stream-provider env vars — point it at a host built from the custom-worker template to test your plugins against the dev cluster. Never emitted by `aspire publish`.
- The E2E `e2e-split-roles` leg uses both (with the test-only `Fleans.E2E.PluginHost`); see [`regression-testing.md`](regression-testing.md#split-role-leg-e2e-splitroles).

Observed in that leg — know before relying on the contract:

- Engine-bundled plugins are registered on **both** engine silos (`Fleans.Api` and `Fleans.WorkerHost` call `AddRestCallerPlugin()`), so with a `Core` + `Worker` split `RestCallerHandler` is compatible with — and may activate on — the `core-` silo. Only a plugin compiled solely into one host (e.g. an external `plugin-` host) has deterministic placement.
- A plugin host owns a slice of the reminder-service hash ring, so it MUST run the engine's reminder provider (`Fleans:Reminders:Provider`: the Redis table on `orleans-redis`, or the Postgres table on `fleans-reminders` / `fleans`, as `AddFleansReminders` does). Without one, BPMN timers hashed onto it fail to register (`TypeLoadException: Orleans.IReminderService`); with an in-memory one they lose durability (#788). `Fleans.E2E.PluginHost` wires `UseRedisReminderService` or `UseAdoNetReminderService` from the same key (#886).
- A plugin host must join the engine's membership table (`Fleans:Clustering:Provider`: Redis on `orleans-redis`, or Postgres on `fleans-cluster` / `fleans`) — on another table it forms a cluster of its own. `Fleans.E2E.PluginHost` follows the same key (#885); see [clustering.md](clustering.md).
- `PlacementRoleAssertion` is currently never registered (#787); role mismatches are caught only by the explicit `Fleans:Role` checks in `Fleans.Api` / `Fleans.WorkerHost` / `AddFleansPluginHost`.

Container image name: `fleans-worker` via `<ContainerRepository>` in `Fleans.WorkerHost.csproj`.

## Read-model projector on worker silos (#861)

The read-model projector (`WorkflowInstanceProjector`, see [persistence.md](persistence.md#event-store-journal-snapshots-read-model)) carries `[WorkerPlacement]`, so it runs on the `worker-` / `combined-` silos next to the script executor: its journal reads and read-model writes stay off the core silos that run the workflows, and it scales with `fleans-worker`. There is no projector role or deployment.

- **Worker silos already have what it needs.** `Fleans.WorkerHost` calls `AddFleansPersistence` (event store, read-model writer) and pulls stream queues like every engine silo. Plugin hosts (`plugin-`) are rejected by the filter, and do not load `Fleans.Application` anyway.
- **Nothing new to register.** The pulling agent that delivers `InstanceAdvanced` places the projector, and every silo already registers the worker filter — including `AddFleansPluginHost` hosts built against an older `Fleans.Worker`.
- **Fallback.** With no worker silo in the cluster the filter returns every compatible silo (EventId 11301), so a `Core`-only cluster still projects on its core silos.
- **Trade-off.** Projection shares CPU and the Npgsql pool with script execution, so a burst of scripts delays the read model: lists lag more, instance state does not (it reads the grain until the row says finished).
- Tests: `ProjectorPlacementIntegrationTests` (a `core-` + `worker-` TestCluster: the projector activates on the worker silo, the instance on the core silo). The E2E split-roles leg checks that the listings catch up (`SplitRoleTopologyTests.ReadModel_IsProjected_ByTheWorkerSilo`).

## Placement contract per role

`Fleans:Role` accepts three silo values cluster-wide (`Api` is not a silo and takes no part in placement):

- **`Core`** (silo prefix `core-`) — engine `Fleans.Api` / `Fleans.Web` / `Fleans.Mcp`; hosts `[CorePlacement]` grains.
- **`Worker` / `Combined`** (silo prefix `worker-` / `combined-`) — engine workers (`Fleans.WorkerHost` in publish mode, `Fleans.Api` in Combined dev mode); host `[WorkerPlacement]` grains (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`, `WorkflowInstanceProjector`) and any engine-bundled plugins.
- **`Plugin`** (silo prefix `plugin-`) — **external** custom worker hosts that host ONLY the plugin grains registered via `AddCustomTaskPlugin<T>()`. Engine-internal `[WorkerPlacement]` grains never land here because `WorkerPlacementFilterDirector.HasWorkerRole` rejects the `plugin-` prefix.

`CustomTaskHandlerBase` subclasses MUST NOT carry `[WorkerPlacement]` — they use Orleans default placement, which relies on `GetCompatibleSilos` to pick a silo that has the concrete handler assembly loaded. Per-plugin isolation falls out of grain-class discovery; do not add a custom placement attribute or director for plugin handlers without re-evaluating this invariant.

External plugin hosts call `siloBuilder.AddFleansPluginHost(configuration)` (from `Fleans.Worker.Hosting`) which validates the role config and stamps the `plugin-` silo name. See the template repo at <https://github.com/nightBaker/fleans-custom-worker-example>.

## Placement-types package boundary

`Core*` placement types (`CorePlacementStrategy`, `CorePlacementDirector`, `CorePlacementAttribute`) live in `Fleans.Application.Abstractions/Placement/` (namespace `Fleans.Application.Placement`) so external plugin hosts can register them — `AddFleansPluginHost` in `Fleans.Worker` calls `AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>()` to route `[CorePlacement]` grain activations (e.g. `CustomTaskCatalogGrain`) from a plugin silo back to a `core-`/`combined-` engine silo.

**Directors read the host list from a 5-second cache (`CachedSiloHosts`), never per activation.** `IManagementGrain.GetDetailedHosts` reads the membership table from its storage and is one grain for the cluster; asked per activation it put a membership-table read and a cluster-wide grain on every instance start (~1.3 reads per started instance in the target-architecture load stack). The cached list is intersected with `IPlacementContext.GetCompatibleSilos` (live membership), so a departed silo is never picked; when it has no candidate the director refreshes once before falling back. A new placement director that needs silo names uses the same cache.

`WorkerPlacementAttribute` and `WorkerPlacementFilterStrategy` live beside them in `Fleans.Application.Abstractions/Placement/` (namespace `Fleans.Application.Placement`), because `WorkflowInstanceProjector` in `Fleans.Application` carries `[WorkerPlacement]` and `Fleans.Application` does not reference `Fleans.Worker`. The filter's director and its registration (`WorkerPlacementFilterDirector`, `AddWorkerPlacementFilter`) stay in `Fleans.Worker.Placement`: the director reads `IClusterMembershipService` (Orleans.Runtime), which Abstractions does not reference, and only silo hosts register it. Orleans keys a placement filter by its strategy's type name, so moving the strategy's assembly and namespace does not affect silos running an older `Fleans.Worker`.

The regression test `PluginHostPlacementTests.AddFleansPluginHost_RegistersCoreDirectorAndWorkerFilter` guards that the Core director and the Worker filter stay wired in `AddFleansPluginHost`; any new placement strategy or filter added to plugin-host registration MUST extend that test.

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
