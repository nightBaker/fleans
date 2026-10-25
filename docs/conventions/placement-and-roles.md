# Cluster roles & grain placement

## The `Fleans:Role` config knob

`Fleans.Api` reads `Fleans:Role` at startup. Values: `Core`, `Worker`, `Combined`, `Projector` (case-insensitive, default `Combined`; invalid values throw). The role is stamped into `SiloOptions.SiloName` as `{role}-{machine}-{guid}` so other silos see it via Orleans membership. With `Fleans:Kubernetes:Enabled=true` the silo name is the **pod name** instead, which must then start with `{role}-` (startup throws otherwise) — see [clustering.md](clustering.md#kubernetes-hosting-fleanskubernetesenabled).

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
- wires the same persistence/streaming/Redis stack as `Fleans.Api`.

It is registered with Aspire **in publish mode** (`builder.ExecutionContext.IsPublishMode`), so `dotnet run --project Fleans.Aspire` keeps the original 3-process dev topology and `aspire publish -t kubernetes` / `-t docker-compose` emits a fourth `fleans-worker` deployment alongside `fleans-core` (Api), `fleans-management` (Web), and `fleans-mcp` (Mcp).

### Running the split topology locally

- `FLEANS_SPLIT_ROLES=true` (dev only) mirrors publish: `fleans-core` defaults to `Core` and `fleans-worker` is registered with `Worker` (Kestrel on a dynamic port — WorkerHost has no launch profile). It also registers `fleans-projector` (`Fleans.Api` with `Projector`, no launch profile, dynamic port; see [Read-model projector silos](#read-model-projector-silos-projector-861)). `FLEANS_ROLE` still overrides the core role.
- `FLEANS_PLUGIN_HOST_PROJECT=<absolute .csproj path>` (dev only) adds a `fleans-plugin-host` resource with `Fleans__Role=Plugin`, the Orleans cluster reference and the stream-provider env vars — point it at a host built from the custom-worker template to test your plugins against the dev cluster. Never emitted by `aspire publish`.
- The E2E `e2e-split-roles` leg uses both (with the test-only `Fleans.E2E.PluginHost`); see [`regression-testing.md`](regression-testing.md#split-role-leg-e2e-splitroles).

Observed in that leg — know before relying on the contract:

- Engine-bundled plugins are registered on **both** engine silos (`Fleans.Api` and `Fleans.WorkerHost` call `AddRestCallerPlugin()`), so with a `Core` + `Worker` split `RestCallerHandler` is compatible with — and may activate on — the `core-` silo. Only a plugin compiled solely into one host (e.g. an external `plugin-` host) has deterministic placement.
- A plugin host owns a slice of the reminder-service hash ring, so it MUST run the engine's reminder provider (`Fleans:Reminders:Provider`: the Redis table on `orleans-redis`, or the Postgres table on `fleans-reminders` / `fleans`, as `AddFleansReminders` does). Without one, BPMN timers hashed onto it fail to register (`TypeLoadException: Orleans.IReminderService`); with an in-memory one they lose durability (#788). `Fleans.E2E.PluginHost` wires `UseRedisReminderService` or `UseAdoNetReminderService` from the same key (#886).
- A plugin host must join the engine's membership table (`Fleans:Clustering:Provider`: Redis on `orleans-redis`, or Postgres on `fleans-cluster` / `fleans`) — on another table it forms a cluster of its own. `Fleans.E2E.PluginHost` follows the same key (#885); see [clustering.md](clustering.md).
- `PlacementRoleAssertion` is currently never registered (#787); role mismatches are caught only by the explicit `Fleans:Role` checks in `Fleans.Api` / `Fleans.WorkerHost` / `AddFleansPluginHost`.

Container image name: `fleans-worker` via `<ContainerRepository>` in `Fleans.WorkerHost.csproj`.

## Read-model projector silos (`Projector`, #861)

`Fleans:Role=Projector` turns a `Fleans.Api` process into a `projector-` silo for the read-model projector (`WorkflowInstanceProjector`, see [persistence.md](persistence.md#event-store-journal-snapshots-read-model)). It is the `fleans-projector` deployment: Helm `projector.enabled`, Aspire `FLEANS_SPLIT_ROLES=true`; there is no separate image.

- **Placement: `[ProjectorPlacement]`** (`ProjectorPlacementStrategy` / `ProjectorPlacementDirector` in `Fleans.Application.Abstractions/Placement/`, beside the `Core*` triplet). The director picks an active, compatible `projector-` silo when there is one, else a `core-` / `combined-` silo (the default and dev topologies), else any compatible silo (EventId 11302). It is a placement *strategy* like `[CorePlacement]`, not a filter: it needs active silo names, and Abstractions only has `IManagementGrain`, not `IClusterMembershipService` (Orleans.Runtime), which a synchronous filter would need.
- **Every silo that pulls stream queues registers the director** (`Fleans.Api`, `Fleans.WorkerHost`, `AddFleansPluginHost`, the test silos). The pulling agent that delivers `InstanceAdvanced` is the caller that activates the projector, and Orleans resolves placement on the caller: a silo without the director throws `KeyNotFoundException`. `PluginHostPlacementTests.AddFleansPluginHost_RegistersCoreDirectorAndWorkerFilter` guards the plugin host. External plugin hosts get it from `AddFleansPluginHost` in the same Fleans version, so engine and plugin hosts upgrade together.
- **A projector silo hosts only the projector.** `Fleans.Api` post-configures `GrainTypeOptions` with `ProjectorSilo.KeepOnlyProjectorGrains`, which drops every other Fleans grain class. The silo's manifest then lists no other Fleans grain, so `GetCompatibleSilos` never offers it for them — including grains with Orleans' default placement (custom-task handlers), which `[CorePlacement]` / `[WorkerPlacement]` alone would not keep away. It also skips `AddRestCallerPlugin()`, so it never advertises a plugin in the catalog. Orleans' own grains (management, streaming, reminders) stay.
- **Infrastructure stays.** It still joins the membership table, serves a slice of the reminder ring and the stream queues, and runs the persistence stack (it reads the journal and writes the read model). It remains a client gateway (Fleans.Api's default); nothing routes there on purpose.
- Tests: `ProjectorPlacementTests` (director and grain-set unit tests) and `ProjectorSiloIntegrationTests` (a `combined-` + `projector-` TestCluster: the projector activates on the projector silo, no other Fleans grain does). The E2E split-roles leg runs `fleans-projector` and checks the listings catch up (`SplitRoleTopologyTests.ReadModel_IsProjected_ByTheProjectorSilo`).

## Placement contract per role

`Fleans:Role` accepts these values cluster-wide:

- **`Core`** (silo prefix `core-`) — engine `Fleans.Api` / `Fleans.Web` / `Fleans.Mcp`; hosts `[CorePlacement]` grains.
- **`Worker` / `Combined`** (silo prefix `worker-` / `combined-`) — engine workers (`Fleans.WorkerHost` in publish mode, `Fleans.Api` in Combined dev mode); host `[WorkerPlacement]` grains (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`) and any engine-bundled plugins.
- **`Projector`** (silo prefix `projector-`) — `Fleans.Api` hosting only the read-model projector (`[ProjectorPlacement]`, section above). Neither `HasCoreRole` nor `HasWorkerRole` accepts the prefix.
- **`Plugin`** (silo prefix `plugin-`) — **external** custom worker hosts that host ONLY the plugin grains registered via `AddCustomTaskPlugin<T>()`. Engine-internal `[WorkerPlacement]` grains never land here because `WorkerPlacementFilterDirector.HasWorkerRole` rejects the `plugin-` prefix.

`CustomTaskHandlerBase` subclasses MUST NOT carry `[WorkerPlacement]` — they use Orleans default placement, which relies on `GetCompatibleSilos` to pick a silo that has the concrete handler assembly loaded. Per-plugin isolation falls out of grain-class discovery; do not add a custom placement attribute or director for plugin handlers without re-evaluating this invariant.

External plugin hosts call `siloBuilder.AddFleansPluginHost(configuration)` (from `Fleans.Worker.Hosting`) which validates the role config and stamps the `plugin-` silo name. See the template repo at <https://github.com/nightBaker/fleans-custom-worker-example>.

## Placement-types package boundary

`Core*` placement types (`CorePlacementStrategy`, `CorePlacementDirector`, `CorePlacementAttribute`) live in `Fleans.Application.Abstractions/Placement/` (namespace `Fleans.Application.Placement`) so external plugin hosts can register them — `AddFleansPluginHost` in `Fleans.Worker` calls `AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>()` to route `[CorePlacement]` grain activations (e.g. `CustomTaskCatalogGrain`) from a plugin silo back to a `core-`/`combined-` engine silo.

**Directors read the host list from a 5-second cache (`CachedSiloHosts`), never per activation.** `IManagementGrain.GetDetailedHosts` reads the membership table from its storage and is one grain for the cluster; asked per activation it put a membership-table read and a cluster-wide grain on every instance start (~1.3 reads per started instance in the target-architecture load stack). The cached list is intersected with `IPlacementContext.GetCompatibleSilos` (live membership), so a departed silo is never picked; when it has no candidate the director refreshes once before falling back. A new placement director that needs silo names uses the same cache.

`Worker*` placement types (`WorkerPlacementFilterStrategy`, `WorkerPlacementFilterDirector`, `WorkerPlacementAttribute`, `AddWorkerPlacementFilter`) live in `Fleans.Worker.Placement` — they do NOT need to be in Abstractions today because `AddFleansPluginHost` is itself in `Fleans.Worker`, and the engine project refs (`Fleans.Api`, `Fleans.WorkerHost`) already pull `Fleans.Worker` so their own `AddWorkerPlacementFilter()` calls compile.

**If a future engine grain inside `Fleans.Application` is annotated with `[WorkerPlacement]`, the entire `Worker*` placement triplet must also be promoted to `Fleans.Application.Abstractions/Placement/` with namespace preserved (`Fleans.Worker.Placement` → mirror Core* layout)** — the current asymmetry is contingent on `grep -rE '\[WorkerPlacement\]' src/Fleans/Fleans.Application` returning empty, and breaks the moment that contract changes.

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
