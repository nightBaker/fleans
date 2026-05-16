# Fleans

BPMN workflow engine built on Orleans.

## Main Rule

**After completing each feature or fix, update this CLAUDE.md** with any lessons learned, patterns discovered, or pitfalls encountered during the work. The goal is to capture hard-won knowledge so the same problems are never solved twice. Add entries to the relevant section (conventions, constraints, lessons learned, etc.) or create a new section if needed.

**After completing each feature or fix, also update the website documentation** under `website/src/content/docs/` — see the **Documentation rule** in the *Documentation Website* section below for the routing details.

## Build & Test

From `src/Fleans/`:

```bash
dotnet build
dotnet test
```

**Context management:** Do not read test files into context until you are at the test verification step (i.e., writing or modifying tests, or verifying that tests pass). Test output logs can be large — avoid loading them prematurely.

Run the full stack (Api + Web + Redis) via Aspire:

```bash
dotnet run --project Fleans.Aspire
```

## Container Builds

Container images are produced by the .NET SDK's built-in `SdkContainerSupport` — no Dockerfiles. Each deployable service has a `<ContainerRepository>fleans-<svc></ContainerRepository>` in its csproj; `src/Fleans/Directory.Build.props` is the single source of truth for `<VersionPrefix>` and `<ContainerImageTag>`. Plugin NuGet packages (`Fleans.Domain.Abstractions`, `Fleans.Application.Abstractions`, `Fleans.Worker`, `Fleans.Plugins.RestCaller`) share the engine's `<VersionPrefix>` track — every release bumps every plugin even if its source is bit-identical.

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj /t:PublishContainer /p:Version=0.1.0-test
# Same for Fleans.Web, Fleans.WorkerHost, Fleans.Mcp.

aspire publish --project Fleans.Aspire -t docker-compose -o out/compose
aspire publish --project Fleans.Aspire -t kubernetes  -o out/k8s
```

`Fleans.Aspire/Program.cs` registers `Fleans.WorkerHost` only in publish mode, so `dotnet run` keeps the dev 3-process topology while `aspire publish` emits api/web/worker/mcp + Redis (+ optional Postgres/Kafka). `Aspire.Hosting.Kubernetes` ships preview-only — pinned to `13.2.3-preview.1.26217.6`; bump together with the rest of the Aspire 13.2.3 stack.

## Git Workflow

- **Never commit directly to `main`.** Always create a feature branch for any new feature, bug fix, or change.
- Branch naming: `feature/<short-description>` or `fix/<short-description>`
- Open a PR from the feature branch to `main`, merge back. CI runs build + test on PRs.

## Documentation Website

The public docs site lives in `website/` — an Astro + Starlight project deployed to GitHub Pages via `.github/workflows/deploy-website.yml` (Node 22, deploy job gated to `main`).

- Content: `website/src/content/docs/` (`guides/`, `concepts/`, `reference/`, `index.mdx`)
- Theme: `website/src/styles/custom.css` — palettes scoped to `:root[data-theme='light']` and `:root[data-theme='dark']`
- Local dev: `cd website && npm install && npm run dev`
- Build check: `npm run build` (must pass before merging)

**Documentation is part of "done", not a follow-up task.** Any new feature, BPMN element, API endpoint, or user-facing behavior MUST be reflected in the docs site in the same PR.

**Documentation scope split.** The two surfaces have non-overlapping audiences and MUST stay separated:

- **`README.md` + `CLAUDE.md` + `docs/plans/` (this repo)** — for contributors working with the Fleans **source tree**. Covers cloning, building, running tests, internal architecture, design docs, source-level conventions, manual test plans, release runbooks. Anything that assumes the reader has `git clone`d this repo belongs here.
- **`website/src/content/docs/` (the public docs site)** — for users consuming a **released** Fleans artifact: pulling container images, running the Helm chart or compose bundle, calling the REST API, and extending the engine via published NuGet plugin packages. Anything that should still make sense to a reader who has never seen the source tree belongs here. "Writing custom-task plugins" lives here because plugin authors consume `Fleans.Worker` from nuget.org, not from source.

When deciding where a new doc belongs, ask: *does it require working in the source checkout, or does it only require a released artifact?* Source-checkout docs → repo `README.md` / `CLAUDE.md` / `docs/plans/`. Released-artifact docs → website. Quick Start on the website MUST use a released artifact (container pull, Helm install) — not `git clone && dotnet run`, which is a source-tree workflow.

**Tabs for interchangeable approaches.** When a page documents two or more **interchangeable** tools or commands that produce the same outcome (e.g. `gh release download` vs `curl -LO`, `docker compose up` vs `helm install`, `apt` vs `brew`), use Starlight's [`<Tabs>`](https://starlight.astro.build/components/tabs/) component instead of stacking *"Or, without X:"* sections — keeps the page compact and lets the reader focus on the path they actually use. Convention:

- Pages that import components must use the `.mdx` extension. Add `import { Tabs, TabItem } from '@astrojs/starlight/components';` after the frontmatter.
- Set a `syncKey` so the tool choice persists across pages (e.g. `<Tabs syncKey="release-download-tool">` on every gh-vs-curl block, `syncKey="package-manager"` on every npm-vs-yarn block).
- **Don't use tabs for non-interchangeable content** — dev vs prod configs, SQLite vs PostgreSQL, compose vs Helm are *different workflows* the reader needs to see both of, not a one-of-two choice.

For website-build infrastructure (Hero BPMN diagram regeneration, 3D landing background, load-test publishing rule), see [`website/README.md`](website/README.md).

## How to Add a New BPMN Activity

1. Create the activity class in `Fleans.Domain/Activities/` — extend the existing `Activity` base class
2. Register it in `Fleans.Infrastructure/Bpmn/BpmnConverter.cs` so BPMN XML maps to it
3. Add tests in `Fleans.Domain.Tests/` using Orleans `TestCluster`. **Every activity must have tests that verify workflow state after both completion and failure** — see `ScriptTaskTests.cs` and `TaskActivityTests.cs` for the pattern:
   - **Completion tests:** completed activities list, variable merging (count + values), activity instance marked completed, no active activities remain, sequential chaining
   - **Fail tests:** error state set with correct code/message (500 for generic Exception, 400 for BadRequestActivityException), failed activity still transitions to next activity, no variables merged on failure
4. Update the BPMN elements table in `README.md`

## How to Add a Custom Task Plugin

A *custom task* is a `<bpmn:serviceTask type="…">` whose execution is supplied by a user-written plugin grain on a Worker silo. Use this — **not** "Add a New BPMN Activity" — when the new behavior is plugin-shaped (REST call, email, custom external system).

1. Add a new project (e.g. `Fleans.Plugins.MyThing`) referencing `Fleans.Worker`. Inside it, write a class deriving from `Fleans.Worker.CustomTasks.CustomTaskHandlerBase`. Override `TaskType` and `ExecuteAsync(...)`. The base class carries `[ImplicitStreamSubscription("events")]` and `[WorkerPlacement]` — subclasses inherit both.
2. Throw `Fleans.Domain.Errors.CustomTaskFailedActivityException(int code, string message)` from `ExecuteAsync` to fail with a typed error; any other thrown exception fails with code 500.
3. The plugin's `ExecuteAsync` returns an `IDictionary<string, object?>`. Output mappings (`<zeebe:output source="=__response.body" target="…"/>`) walk that dictionary.
4. Expose a DI extension method on the plugin assembly:
   ```csharp
   public static IServiceCollection AddMyThingPlugin(this IServiceCollection services) =>
       services.AddCustomTaskPlugin<MyThingHandler>(taskType: "my-thing", displayName: "My Thing");
   ```
   Plugin authors who want their plugin to live in the catalog UI must call this from the Worker silo's host registration.
5. Tests: write unit tests for the plugin's logic (call `ExecuteAsync` directly with stub inputs); end-to-end TestCluster integration is exercised by manual test plan #37 once a real plugin ships.
6. Documentation: update `website/src/content/docs/concepts/custom-tasks.md` with the plugin's parameter schema and any limitations.

## How to Add a New API Endpoint

Add it to `Fleans.Api/Controllers/WorkflowController.cs`. DTOs go in `Fleans.ServiceDefaults/`.

## Architecture Principles

- **Always respect SOLID, DDD, and Clean Architecture principles.** Keep domain logic in `Fleans.Domain` free of infrastructure concerns. Depend on abstractions, not concretions. Ensure single responsibility across classes and methods. Model the domain with aggregates, value objects, and domain events where appropriate.
- **Domain state methods must be self-contained.** If a flag and a timestamp (or any related fields) always change together, they belong in the same domain state method. Grains must not set state properties directly — call a domain method that encapsulates the full state transition atomically. This prevents inconsistent state from partial updates.

## Code Conventions

- Follow existing patterns — records for immutable DTOs, `[GenerateSerializer]` on anything crossing grain boundaries. Collections inside serialized records must be `List<T>` (or another Orleans-copyable collection) — `ReadOnlyArray<T>` produced by C# collection expressions (`[x, y, z]` assigned to a non-mutable target) has no Orleans copier and fails at runtime.
- ExpandoObject + Newtonsoft.Json for dynamic workflow variable state
- Tests use MSTest + Orleans.TestingHost, AAA pattern. Activity tests must verify both post-completion and post-failure state. Query state via `workflowInstance.GetState()` after completion/failure — never hold grain references from before completion to assert on.
- **Admin UI (Fleans.Web) communicates with Orleans grains directly via `WorkflowEngine` service** — not through HTTP API endpoints. The Web app runs as Blazor Server (InteractiveServer), so Razor components execute server-side and can call grains directly. Do not add API endpoints for admin UI functionality.
- **WorkflowInstance partial class layout** — see the header comment at the top of `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` for which methods go in which of the three partial files (`.cs` / `.Infrastructure.cs` / `.Logging.cs`).
- **Logging: always use `[LoggerMessage]` source generators** instead of `ILogger.Log*()` extension methods. Define log methods as `private partial void` on `partial` classes. New `[LoggerMessage]` declarations go in `WorkflowInstance.Logging.cs`. EventId ranges are documented in `docs/plans/2026-02-08-structured-workflow-logging.md`.
- **WorkflowInstance state changes** flow through `DrainAndRaiseEvents()`, which drains uncommitted events from the aggregate, calls `RaiseEvent(event)` for each, then `ConfirmEvents()` to persist. Never use `WriteStateAsync()` in WorkflowInstance. Other grains still use `WriteStateAsync()` with their own IPersistentState storage.
- **Log all workflow instance state changes.** Every grain method that mutates state (adds/removes activities, changes condition results, completes/fails instances) must have a `[LoggerMessage]` log call. No silent state mutations.
- **Fluent UI Blazor (Fleans.Web)**: Only use components that exist in the library (https://www.fluentui-blazor.net/). Use `IconStart`/`IconEnd` parameters on `FluentButton` — never place `<FluentIcon>` as child content. Use the `Loading` parameter for buttons with loading states.
- **Cache-busting for local Razor host assets**: every `<script>` or `<link>` in `App.razor` (or any other Razor host file) that references an asset under `wwwroot/` MUST go through `@Assets["..."]` so the URL carries a content hash. Externally-versioned CDN URLs (e.g. `https://unpkg.com/bpmn-js@17.11.1/...`) are exempt because the version segment in the URL already serves the same purpose. Without this, a published JS API addition can be invisible to browsers that cached the old file (root cause of #373).
- **Core / Worker role split (`Fleans:Role`)**: `Fleans.Api` reads `Fleans:Role` at startup (values: `Core`, `Worker`, `Combined` — case-insensitive, default `Combined`; invalid values throw). The role is stamped into `SiloOptions.SiloName` as `{role}-{machine}-{guid}` so other silos see it via Orleans membership. `Fleans.Worker` hosts the `[StatelessWorker]` script/condition grain **implementations** (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`); their interfaces remain in `Fleans.Application` so callers don't need a Worker reference. **When adding a new worker-type grain**, put the implementation in `Fleans.Worker` and keep the interface next to the caller in `Fleans.Application`. The split is structural only (separate assembly, separate role config) — there is no runtime placement filtering, so a `Core`-tagged silo will still host a worker grain if one is needed there. Aspire stamps `Fleans__Role` on every project: dev mode tags `fleans-core` as `Combined` (3-process topology must host worker grains in-process), publish mode tags it `Core` to match `deployment-core.yaml` in the Helm chart. Set `FLEANS_ROLE=<value>` in the Aspire host's environment to override either default — useful for testing the Core/Worker split locally without editing source.
- **`Fleans.WorkerHost`** is the dedicated deployable for the Worker role — a thin Web SDK Exe that boots an Orleans silo with `Fleans:Role=Worker` by default, references the `Fleans.Worker` class library for grain implementations + placement directors, and wires the same persistence/streaming/Redis stack as `Fleans.Api`. It is registered with Aspire **only in publish mode** (`builder.ExecutionContext.IsPublishMode`), so `dotnet run --project Fleans.Aspire` keeps the original 3-process dev topology and `aspire publish -t kubernetes` / `-t docker-compose` emits a fourth `fleans-worker` deployment alongside `fleans-core` (Api), `fleans-management` (Web), and `fleans-mcp` (Mcp). Container image name: `fleans-worker` via `<ContainerRepository>` in `Fleans.WorkerHost.csproj`.
- **Three-role placement contract.** `Fleans:Role` accepts three values cluster-wide:
  - `Core` (silo prefix `core-`) — engine `Fleans.Api` / `Fleans.Web` / `Fleans.Mcp`; hosts `[CorePlacement]` grains.
  - `Worker` / `Combined` (silo prefix `worker-` / `combined-`) — engine workers (`Fleans.WorkerHost` in publish mode, `Fleans.Api` in Combined dev mode); host `[WorkerPlacement]` grains (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`) and any engine-bundled plugins.
  - `Plugin` (silo prefix `plugin-`) — **external** custom worker hosts that host ONLY the plugin grains registered via `AddCustomTaskPlugin<T>()`. Engine-internal `[WorkerPlacement]` grains never land here because `WorkerPlacementDirector.HasWorkerRole` rejects the `plugin-` prefix.

  `CustomTaskHandlerBase` subclasses MUST NOT carry `[WorkerPlacement]` — they use Orleans default placement, which relies on `GetCompatibleSilos` to pick a silo that has the concrete handler assembly loaded. Per-plugin isolation falls out of grain-class discovery; do not add a custom placement attribute or director for plugin handlers without re-evaluating this invariant.

  External plugin hosts call `siloBuilder.AddFleansPluginHost(configuration)` (from `Fleans.Worker.Hosting`) which validates the role config and stamps the `plugin-` silo name. See the template repo at `https://github.com/nightBaker/fleans-custom-worker-example`.
- **Custom-worker host template (external repo)** — the "host your own custom-task plugins" worked example lives at **[github.com/nightBaker/fleans-custom-worker-example](https://github.com/nightBaker/fleans-custom-worker-example)**, marked as a GitHub template (`Use this template` button). It depends only on `Fleans.Worker` + plugin packages from nuget.org (no `Fleans.Application` / `Fleans.Domain` / `Fleans.Infrastructure` / persistence references — structurally enforced by a CI leaf-package guard). The example was extracted from this repo in v0.2.0+ once the leaf-package refactor made `Fleans.Worker` consumable from outside the engine source tree. Plugin authors click "Use this template" to scaffold their own host, set `Fleans:Role=Plugin` (the new third role — silo prefix `plugin-`, see the *Three-role placement contract* bullet above), call `siloBuilder.AddFleansPluginHost(builder.Configuration)` from `Fleans.Worker.Hosting` in their `Program.cs` (validates the role and stamps the `plugin-<machine>-<guid>` silo name), register their plugins via `services.AddXxxPlugin()`, and ship the resulting container image alongside the engine. This repo's release pipeline does NOT publish a `fleans-custom-worker` image — the image matrix is `api/web/worker/mcp` only.
- **Plugin-author NuGet stack (leaf-package design):** Three NuGet packages compose the plugin-author surface; the dependency closure is strictly hierarchical so plugin authors get only what they need.
  - **`Fleans.Domain.Abstractions`** is the true leaf — depends only on `Microsoft.Orleans.Sdk`. Holds the marker interface (`IDomainEvent`), event records carried on Orleans streams (`ExecuteCustomTaskEvent`), the value records those events reference (`InputMapping`, `OutputMapping`), and the exception hierarchy plugins may throw (`ActivityException`, `CustomTaskFailedActivityException`, `BadRequestActivityException`, `ActivityErrorState`).
  - **`Fleans.Application.Abstractions`** depends on `Fleans.Domain.Abstractions` (NOT `Fleans.Domain`). Holds grain interfaces that span Core↔Worker silos (`IConditionExpressionEvaluatorGrain`, `IScriptExecutorGrain`, `ICustomTaskCatalogGrain`, narrow `IWorkflowInstanceCallback`), custom-task schema records (`CustomTaskParameterSchema`, `CustomTaskRegistration`, `CustomTaskCatalogEntry`), `MappingResolver`, `WorkflowLoggingContext`/`WorkflowContextKeys`, and stream-namespace constants (`WorkflowEventStreams`).
  - **`Fleans.Worker`** depends only on `Fleans.Application.Abstractions` (transitively pulls Domain.Abstractions). Carries `CustomTaskHandlerBase`, `[WorkerPlacement]`, placement directors, and the worker-side grain implementations. **No reference to `Fleans.Application`, `Fleans.Domain`, `Fleans.Infrastructure`, or persistence projects.**
  - **Refactor convention — namespace-preserving moves:** When relocating types into an abstractions package, **preserve the source namespace**. Package name ≠ namespace: the namespace is the API contract; the package is the shipping unit. Precedent: `Microsoft.Extensions.Logging.Abstractions` places types in the `Microsoft.Extensions.Logging` namespace. Preserving namespaces means zero `using`-directive churn across callers, Orleans `[GenerateSerializer]` wire format stays bit-identical (FQN unchanged), and `[Alias("…")]` declarations carry through unchanged. The cycle/leak invariants are guarded post-refactor; if you find yourself adding a `<ProjectReference Include="..\Fleans.Domain\…" />` to `Fleans.Application.Abstractions.csproj` or `<ProjectReference Include="..\Fleans.Application\…" />` to `Fleans.Worker.csproj`, that means a type should have moved instead — extend the abstractions package and revisit.
  - **Reflection-by-assembly trap:** Code that scans `typeof(IDomainEvent).Assembly.GetTypes()` (or any other anchor type in `*.Abstractions`) now sees only the few types that live in `Fleans.Domain.Abstractions.dll`, not the dozens that stayed in `Fleans.Domain.dll`. `Fleans.Persistence.Events.EventTypeRegistry` was the one caller affected and now scans both assemblies, anchoring `Fleans.Domain` via `typeof(WorkflowStarted).Assembly`. When adding new reflective scans over `IDomainEvent` implementations (or any other marker interface that lives in an abstractions package), scan **both** the abstractions assembly and the concrete-types assembly, anchored by a stable type in each.
  - **Stays in `Fleans.Application` (not in Abstractions):** the full `IWorkflowInstanceGrain` interface (uses heavy `Fleans.Domain.States.*` types beyond what Worker needs — Worker calls back via the narrow `IWorkflowInstanceCallback` that `IWorkflowInstanceGrain` extends), and `WorkflowLoggingScopeFilter` (an `IIncomingGrainCallFilter` that references `IWorkflowInstanceGrain` for its skip-self check).
- **Compose bundle post-processing (`src/Fleans/scripts/postprocess-compose-bundle.sh`)**: `aspire publish -t docker-compose` emits a YAML that is structurally correct but unusable out of the box — every parameter (image refs, ports, secrets) ships as an empty `${VAR}` placeholder, host-port mappings are random, `POSTGRES_DB` is missing so Aspire's `AddDatabase("fleans")` connection string targets a database that never gets created, and there are no `depends_on` / healthcheck declarations so on cold start fleans-core can race postgres and crash its EF Core migration with `Connection refused` (the .NET process then zombies at 99% CPU because other DI services prevent clean exit, leaving the container "Up" but unresponsive — discovered while smoke-testing v0.1.0). The release pipeline runs `postprocess-compose-bundle.sh out/compose <version>` after `aspire publish` to (1) fill `.env` with sensible defaults (version-pinned ghcr.io image refs, container ports `8080`, random base64 redis/postgres passwords, cluster id `fleans`), (2) rewrite `ports:` to fixed host bindings (Web on `8080`, API on `8081`), (3) inject `POSTGRES_DB: fleans` on the postgres service, (4) add a `pg_isready -U postgres -d fleans` healthcheck on postgres, (5) upgrade Aspire's `depends_on { postgres: condition: service_started }` to `condition: service_healthy` so the healthcheck is actually consulted, and (6) add `restart: on-failure` to every fleans-* service so they self-heal on transient failures. The script is idempotent — fills lines that match `KEY=$`, skips the postgres healthcheck if one already exists, skips `restart:` injection per-service if already present, and the §5 rewrite is a no-op once the condition has been upgraded. Any new Aspire-emitted parameter that ships empty must get a default added here, otherwise the artifact is broken at first run.
- **Postgres migration race across silos**: every silo (Api/Web/Mcp/Worker) calls `EnsureDatabaseSchemaAsync` at startup, which under Postgres calls `MigrateAsync`. EF Core's per-migration lock is acquired only during `__EFMigrationsHistory` writes, not while running the migration SQL — so two silos that both observe a migration as pending can both try to `CREATE TABLE` and the loser fails with `relation "X" already exists`. `EnsureDatabaseSchemaAsync` wraps `MigrateAsync` in a session-level `pg_advisory_lock(8723547283)` (via a pinned connection from `OpenConnectionAsync`) to serialize concurrent migration attempts cleanly. The lock key is arbitrary but must be the same across all silos. SQLite uses `EnsureCreatedIgnoreRaces` and is unaffected.
- **BPMN Editor tabs (`/editor`)**: multi-tab state lives in `Editor.razor` (private `tabs: List<TabSession>` + `activeTabId`). Only one `bpmn-js` modeler exists at a time — switching tabs calls `bpmnEditor.getXml` on the outgoing tab and `bpmnEditor.loadXml(incoming.BpmnXml)` on the incoming. Dirty tracking subscribes to bpmn-js `commandStack.changed` via `bpmnEditor.registerDirtyCallback` and flips the active tab's flag (cleared on deploy). Persistence is localStorage-only under key `fleans.editor.tabs.v1` (versioned so future schema changes don't crash old sessions). The cap is 10 tabs; closing the last tab opens a fresh blank one so the editor is never empty.
- **Stream-id sharding by `WorkflowInstanceId`.** `WorkflowEventsPublisher` keys each engine-event stream by `event.WorkflowInstanceId.ToString("D")` for `ExecuteCustomTaskEvent`, `ExecuteScriptEvent`, `EvaluateConditionEvent`, `EvaluateActivationConditionEvent`, and `EvaluateCompletionConditionEvent`. Per-instance FIFO ordering is preserved (Orleans guarantees FIFO per stream); cross-instance traffic distributes across `TotalQueueCount` Redis queues and across silos. Scaling `fleans-worker` replicas now provides real parallelism across distinct workflow instances for `CustomTaskHandlerBase` plugin handlers. The default `IDomainEvent` switch branch in `Publish` logs at warning level (`LogUnknownEventType`, EventId 5001) and drops — adding a new engine event type requires a new `switch` case.
- **Subscriber-side stream-id trap.** Implicit-subscription handler grains (`CustomTaskHandlerBase`, `WorkflowExecuteScriptEventHandler`, `WorkflowEvaluateActivationConditionEventHandler`, `WorkflowEvaluateConditionEventHandler`, `WorkflowEvaluateCompletionConditionEventHandler`) MUST reconstruct the stream id from `this.GetPrimaryKeyString()` inside `OnActivateAsync` — hard-coding `nameof(<Event>)` (the old, pre-sharding key) silently breaks dispatch because the grain ends up listening on a different stream than the one Orleans activated it for. Replicate the same pattern for any new `[ImplicitStreamSubscription]` grain. The fallback `else { stream.SubscribeAsync(...) }` branch is intentionally absent — implicit-subscription dispatch always materialises a handle (regressions surface in the broader script/condition/custom-task integration tests, all of which flow through these handlers).
- **BPMN extension namespace policy**: parser accepts three URIs for the engine's extension elements (`taskDefinition`, `ioMapping > input/output`, `subscription`, `expectedOutputs`, multi-instance loop attrs, `correlationKey`): `https://fleans.io/schema/bpmn/1.0` (current `fleans:`), `http://fleans.io/schema/bpmn/fleans` (legacy `fleans:`, kept for back-compat — see `BpmnNamespaces.FleansLegacy`; remove once in-the-wild files are gone), and `http://camunda.org/schema/zeebe/1.0` (`zeebe:`, kept indefinitely so files exported from Camunda's modeler still deploy). All parser sites probe namespaces via `BpmnNamespaces.FindExtensionElement` / `GetExtensionAttributeValue` (probe order: `Fleans`, `FleansLegacy`, `Zeebe`). The editor writes new BPMN with the `fleans:` prefix at the 1.0 URI. Inside `<fleans:expectedOutputs>` the child element is `<fleans:expectedOutput name="…">`; the legacy `<fleans:output name="…">` shape is still parsed but no longer written.

## Design Constraints

- **Each activity instance executes at most once** — every non-boundary activity instance runs exactly once (completes or fails). An activity definition can be visited multiple times (e.g., in a loop), creating a new instance each time. `TimerCallbackGrain` keying uses `hostActivityInstanceId` to distinguish instances of the same activity.
- **Compensation handlers run in isolated child scopes** — each handler gets a fresh variable scope seeded with the compensable activity's completion-time snapshot, overlaying the enclosing scope. After a handler completes successfully, its variable changes MUST be merged back into the enclosing scope before the next handler spawns. Otherwise: (a) later handlers in the walk see stale variables, and (b) compensation side-effects vanish after the walk finishes. `WorkflowExecution.AdvanceCompensationWalkIfHandlerCompleted` emits a `VariablesMerged` event with the handler's full variable map targeting the parent scope's variables ID (root scope's if the walk is at root). Do not break this invariant when refactoring the compensation path.
- **Registration-vs-cleanup error asymmetry (#425)** — registration-path failures (build-time `ProcessRegisterMessage` throws, handler-time `RegisterTimerEffect` Activate throws, handler-time `SubscribeMessageEffect` / `SubscribeSignalEffect` throws) MUST route to `FailActivity` so the workflow surfaces the failure in state. Cleanup-path failures (`UnsubscribeMessageEffect`, `UnsubscribeSignalEffect`, `UnregisterTimerEffect`, `ThrowSignalEffect`) MUST be log-only — the activity that owned the registration has already completed or been cancelled by the time these run, and failing the workflow now would violate the "Each activity instance executes at most once" invariant. Effect handlers in `Fleans.Application/Effects/{Message,Signal,Timer}EffectHandler.cs` enforce this.
- **Stream-redelivery idempotency contract (#569)** — every state-mutating grain method reachable from a `[ImplicitStreamSubscription]` handler MUST be idempotent under at-least-once delivery. The current guarantees come from a layered set of guards: (1) **grain-entry stale-callback guard** `HasActiveEntry(activityInstanceId)` at `Fleans.Application/Grains/WorkflowInstance.cs:315` / `:351` covers `CompleteActivity` / `FailActivity` (the dominant call sites); (2) **domain-entry stale guard** `entry.IsCompleted` at `Fleans.Domain/Aggregates/WorkflowExecution.cs:2135` / `:2186` is defense-in-depth on the same paths; (3) `joinState.HasFired` at `WorkflowInstance.cs:389` covers `CompleteActivationCondition`; (4) the domain stale-guard `entry.IsCompleted || entry.ActivityInstanceId != hostActivityInstanceId` at `WorkflowExecution.cs:1813` covers `CompleteMultiInstanceEarly`; (5) `CompleteConditionSequence` relies on set-by-key projection idempotency (`SetConditionSequenceResult` at `WorkflowInstanceState.cs:393` and `VariablesMerged → WorkflowVariablesState.Merge` at `WorkflowVariablesState.cs:31`) — both are pure dictionary key-assigns, so re-applying with the same payload is a no-op. The compensation walk's `VariablesMerged → parent-scope` chain is single-entry-protected because `AdvanceCompensationWalkIfHandlerCompleted` runs only after the compensation handler's `CompleteActivity` clears `HasActiveEntry`. **When adding a new state-mutating method on `IWorkflowInstanceCallback` (or any other grain interface called from a stream handler), extend this guard set or prove the new path is naturally idempotent** — silent doubling under redelivery is a production correctness bug, not a test flake.

## Manual Test Plans & Regression

- **Every new feature must have a manual test plan.** After writing the design doc and implementation plan, create a numbered folder under `tests/manual/NN-feature-name/` containing the `.bpmn` fixture(s) and a `test-plan.md` (deploy / start / trigger events / verify checklist). Test plans are verified via Chrome (Web UI) + API calls for messages/signals. See `docs/plans/2026-02-25-manual-test-plan-design.md` for the template.
- **The full regression suite** (43 BPMN scenarios + 16 website scenarios), the BPMN fixture authoring rules, and the manual-test API endpoint reference live in [`tests/manual/README.md`](tests/manual/README.md). That file is the canonical catalog — when adding a new manual test folder, append an entry there.
- Universal backend prerequisite for any plan: Aspire stack running via `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).

## Regression tests

The canonical regression catalog lives in [`tests/manual/README.md`](tests/manual/README.md). Run every numbered entry in both the **BPMN regression suite** and the **Website regression suite** in order; each entry links to a `tests/manual/NN-feature-name/test-plan.md` with the per-step deploy / start / trigger / verify checklist.

**Prerequisites — BPMN suite:** Aspire stack running via `dotnet run --project Fleans.Aspire` (from `src/Fleans/`); a clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`); Web UI reachable at `https://localhost:7124`; API origin `https://localhost:7140`.

**Prerequisites — Website suite:** `cd website && npm install` has been run at least once; `npx playwright install chromium` has been run at least once (only needed for plans that shell out to Playwright); ports `4321` / `4327` / `4328` free.

**How to run:**

1. Open `tests/manual/README.md` and treat each numbered entry under "BPMN regression suite" and "Website regression suite" as one regression step.
2. For each entry, open its linked `test-plan.md` and execute the checklist end-to-end against the PR branch (not `main`).
3. Record the result as `PASSED`, `FAILED`, `BUG` (new regression — file an issue), or `KNOWN BUG` (matches a `> **KNOWN BUG:** …` note inside the linked plan; counts as PASSED for promotion purposes).
4. Aggregate results into the standard "Manual Regression Test Results" PR-issue comment, then promote (Review by Human) or reject (Ready) per the manual-regression-testing skill's normal flow.

## Cutting a Release

The release pipeline at `.github/workflows/release.yml` triggers on `git push origin v<SemVer>`. This is the maintainer runbook for cutting a release. Manual test plan: `tests/manual/42-release-pipeline/test-plan.md`.

### Pre-tag checklist

1. **Manual regression suite green** — run the full BPMN regression list (`tests/manual/01-…/` through the latest entry) plus the website regression list against `main`. Document any KNOWN-BUG verdicts in the release notes draft.
1a. **Verify `Universley.OrleansContrib.StreamsProvider.Redis` pin** in `Fleans.ServiceDefaults.csproj` matches what the manual regression actually ran against. The package uses date-based versioning (`YYYY.M.D`) and is not SemVer; an unintentional version drift could ship a different Redis-streaming adapter than the one the regression validated.
2. **Version bump in `Directory.Build.props`** — `<VersionPrefix>` only needs a hand-bump for *local development builds* (so dev images get reasonable tags). The release workflow overrides `/p:Version=<git-tag-without-v>` regardless, so the assembly + container tag always match the git tag.
3. **Changelog draft** — start from `git log v<PREV>..main --oneline --no-merges`. The workflow auto-generates release notes via `gh release create --generate-notes`; a hand-authored "Highlights" section makes the post readable.
4. **Pre-release dry-runs — run BOTH workflows (the sentinels are intentionally different):**
   - **`release.yml` dry-run:** `gh workflow run release.yml -f version=0.0.0-rc-test`. Uploads compose-zip + helm-tgz artifacts but skips `gh release create` (gated on `is_dispatch_dry_run`). Download the artifacts and smoke-test compose + helm against a `kind` cluster.
   - **`nuget-publish.yml` dry-run:** `gh workflow run nuget-publish.yml -f version=0.0.0-ci-test`. Packs the 4 plugin packages and uploads them as workflow artifacts but skips the actual `dotnet nuget push` (gated by `inputs.version != '0.0.0-ci-test'` on push/pack steps).

### Tag command

```bash
git tag v0.1.0-beta && git push origin v0.1.0-beta
```

The release workflow runs setup → images → compose → helm-package → release in a single CI run. The same `v<SemVer>` tag push triggers `nuget-publish.yml` in parallel; that workflow waits for the GitHub Release object to exist before publishing to nuget.org, so a failed `release.yml` never produces orphan NuGet packages. (We can't use `on.release.published` here: releases created by the default `GITHUB_TOKEN` do not trigger downstream workflows — GitHub anti-recursion safeguard.)

### Post-tag verification

1. **Workflow run green** — `gh run list --workflow=release.yml --limit 1` should show the tagged run as ✅ on every job.
2. **All 4 images pullable, multi-arch** — `docker buildx imagetools inspect ghcr.io/nightbaker/fleans-{api,web,worker,mcp}:0.1.0-beta` should resolve `linux/amd64` + `linux/arm64`.
3. **Release assets attached** — `gh release view v0.1.0-beta --json assets` should list `docker-compose-v0.1.0-beta.zip` + `fleans-0.1.0-beta.tgz`.
4. **Notes look right** — auto-generated notes group commits per `.github/release.yml` categories.
5. **NuGet publish triggered + green** — pushing the tag fires `nuget-publish.yml` in parallel with `release.yml`. It blocks on the GitHub Release existing (max 20 min wait) before pushing to nuget.org. Verify via `gh run list --workflow=nuget-publish.yml --limit 1`. Verify each of the 4 packages on nuget.org: `Fleans.Domain.Abstractions`, `Fleans.Application.Abstractions`, `Fleans.Worker`, `Fleans.Plugins.RestCaller`.
6. **Cosign verify smoke test** — pick one of the published images and verify the signature against Sigstore. The output should include a `Bundle` block with a `tlogEntries[0].logIndex` proving the signature was logged to the public Rekor transparency log.

   ```bash
   cosign verify \
     --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     ghcr.io/nightbaker/fleans-api:0.1.0-beta | jq
   ```

   For the helm chart, verify the blob signature using the `.sig` and `.crt` attached to the release (see `self-host-helm.md` for the exact command):

   ```bash
   gh release download v0.1.0-beta -p 'fleans-0.1.0-beta.tgz*'
   cosign verify-blob \
     --certificate fleans-0.1.0-beta.tgz.crt \
     --signature   fleans-0.1.0-beta.tgz.sig \
     --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     fleans-0.1.0-beta.tgz
   ```

### Rollback

If a release ships broken:

```bash
# 1. Remove the GH release + tag (deleting the release deletes the remote tag too).
gh release delete v0.1.0-beta --cleanup-tag --yes

# Drop the local tag if still present.
git tag -d v0.1.0-beta 2>/dev/null || true

# 2. Delete the four ghcr.io image versions for the broken tag.
#    (`docker buildx imagetools` has no `remove` subcommand — use the GH Packages REST API.)
for IMG in fleans-api fleans-web fleans-worker fleans-mcp; do
  VID=$(gh api "/user/packages/container/$IMG/versions" \
    --jq '.[] | select(.metadata.container.tags | index("0.1.0-beta")) | .id' \
    | head -1)
  if [ -n "$VID" ]; then
    gh api -X DELETE "/user/packages/container/$IMG/versions/$VID"
    echo "Deleted $IMG version $VID (tag 0.1.0-beta)"
  else
    echo "No version of $IMG with tag 0.1.0-beta — skipping"
  fi
done
```

If the org name on ghcr.io ever changes from a user account to an organization, swap `/user/packages/...` for `/orgs/<org>/packages/...`. The token that runs this needs the `delete:packages` scope.

NuGet packages **cannot be deleted** from nuget.org — only unlisted (`dotnet nuget delete <package> <version> --source https://api.nuget.org/v3/index.json -k <KEY>`). If a broken plugin shipped, ship a hotfix release immediately rather than relying on unlisting.

### Documentation rule reminder

Every release that introduces user-visible changes MUST update the self-host guides (`website/src/content/docs/guides/self-host-docker-compose.md`, `guides/self-host-helm.md`) in the same PR per the existing "Documentation rule". The release-asset URLs in those guides reference the *current* tag — bumping `v0.1.0-beta` → `v0.2.0` requires a docs sweep.


## Persistence Providers

Two providers: **SQLite** (default, local dev) and **PostgreSQL** (production/load testing). Selected via configuration — no code changes needed.

- **Config key:** `Persistence:Provider` (values: `Sqlite` | `Postgres`, case-insensitive, default `Sqlite`). `AddFleansPersistence` throws `ArgumentException` on any other value (typos, empty, whitespace) so misconfigured deployments fail fast — mirrors the validation pattern in `FleanStreamingExtensions.cs:26`.
- **Aspire:** set `FLEANS_PERSISTENCE_PROVIDER=Postgres` env var before launch to auto-provision a Postgres container
- **Connection strings:** SQLite uses `FLEANS_SQLITE_CONNECTION` / `FLEANS_QUERY_CONNECTION` env vars. PostgreSQL uses `ConnectionStrings:fleans` (required) and `ConnectionStrings:fleans-query` (optional read replica).
- **Migration strategy:** SQLite uses `EnsureCreated()`. PostgreSQL uses `MigrateAsync()` (migrations applied automatically by `Fleans.Api` on startup).
- **Migrations live per-provider:** `Fleans.Persistence.Sqlite/Migrations/Command/` and `Fleans.Persistence.PostgreSql/Migrations/Command/`. Only command-context migrations are maintained (command and query share the same database).
- **Provider packages:** `Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql` — each registers a `RelationalModelCustomizer` subclass via `ReplaceService<IModelCustomizer>` for provider-specific model tweaks (e.g., SQLite stores `DateTimeOffset` as string; PostgreSQL uses native `timestamptz`).
- **Adding a new provider:** Create a new `Fleans.Persistence.<Provider>` project, implement a `<Provider>ModelCustomizer : RelationalModelCustomizer`, add an `Add<Provider>Persistence()` extension, generate initial migrations, and wire into host `Program.cs` files.
- **Custom-task catalog persistence:** `CustomTaskCatalogGrain` uses `IPersistentState<CustomTaskCatalogState>` keyed by `GrainStorageNames.CustomTaskCatalog`, backed by `EfCoreCustomTaskCatalogGrainStorage` and the `CustomTaskCatalogEntries` table (composite PK on `(TaskType, SiloName)`; `ParameterSchemaJson` stored as a JSON text column via `System.Text.Json`). Test clusters must register memory grain storage for this name in `WorkflowTestBase` alongside the other registries.
- **Test parity (Sqlite ↔ PostgreSQL):** every EF/grain-storage class in `Fleans.Persistence.Tests` is parametrised via `[DataTestMethod] [DataRow(PersistenceProvider.Sqlite)] [DataRow(PersistenceProvider.Postgres)]` against the `Infrastructure/PersistenceTestBase` fixture. Default `dotnet test` runs only the SQLite rows (no Docker). To exercise the PG rows locally:
  ```bash
  cd src/Fleans
  FLEANS_PG_TESTS=1 dotnet test --filter "TestCategory=Postgres"
  ```
  Requires Docker (Testcontainers boots `postgres:16-alpine`). Without `FLEANS_PG_TESTS=1` the PG rows surface as `Inconclusive` (non-failing) — never `Failed`. CI runs the dedicated `PostgreSQL tests` job (`.github/workflows/pg-tests.yml`) on every PR. **Bump `PostgresImage` in `Infrastructure/PostgresContainerFixture.cs` whenever the production deploy target moves** — today it tracks Aspire's `Aspire.Hosting.PostgreSQL` default (PG 16).
- **`Persistence:MaxEventsPerLoad` safety valve (default `1000`):** `EfCoreEventStore.ReadEventsAsync` throws `InvalidOperationException` if the unread event delta exceeds this cap, surfacing broken-snapshotting scenarios before they OOM the silo. Increase via `Persistence__MaxEventsPerLoad` env var for deployments with legitimately large deltas. `FleansPersistenceOptions` lives in `Fleans.Persistence` (not `Fleans.ServiceDefaults`) to avoid a circular project reference.
- **User-task list filter pushdown (audit #415):** `GetPendingUserTasks(assignee, candidateGroup, page)` pushes the assignee/candidateGroup filter to SQL on PostgreSQL via JSON-encoded `LIKE` patterns in raw SQL (`FromSqlInterpolated`); SQLite retains the existing in-memory filter. `EF.Functions.Like` against a value-converted column (the JSON-serialized `IReadOnlyList<string>`) is not usable — EF applies the value converter to the LIKE pattern constant. Use Path C (`FromSqlInterpolated`) for any future column-level JSON filter on the same pattern. Strategy: `IUserTaskFilterStrategy` / `PostgresUserTaskFilterStrategy` in `Fleans.Persistence` / `Fleans.Persistence.PostgreSql`.

## Things to Know

- **Aspire is the startup project**, not Api or Web
- **WorkflowInstance uses JournaledGrain with event sourcing** — events are persisted via EfCoreEventStore, read-side state is projected via EfCoreWorkflowStateProjection (CQRS pattern). Other grains (ProcessDefinition, correlations, timers, start event listeners, user tasks) use IPersistentState with EF Core IGrainStorage.
- **Four stream providers**: **Redis (default, multi-silo-safe, reuses the existing `orleans-redis` container)**, Memory (`FLEANS_STREAMING_PROVIDER=Memory` — in-process only, single-silo debug-only), Kafka (`FLEANS_STREAMING_PROVIDER=Kafka` — separate Kafka cluster), Azure Queue Storage (`FLEANS_STREAMING_PROVIDER=AzureQueue` — Azurite/Azure Storage). Redis is the default because the engine already provisions Redis for clustering + `PubSubStore`, so durable streaming costs zero extra infrastructure. Redis streaming uses the third-party [`Universley.OrleansContrib.StreamsProvider.Redis`](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis) package (MIT, Orleans 10.x-compatible). The version is **pinned** in `Fleans.ServiceDefaults.csproj` — bump deliberately, since the package uses date-based versioning (`YYYY.M.D`) rather than SemVer. **Production operational caveat:** Redis streaming requires Redis persistence (AOF or RDB) enabled, otherwise in-flight messages are lost on Redis restart (same concern operators already have for `PubSubStore`). If the third-party package ever goes unmaintained, the swap-out path is to fork it or build a custom adapter (~530 LoC mirroring `Fleans.Streaming.Kafka`).
- **BPMN coverage is partial** — see the table in `README.md` for what's implemented
- **Design docs** live in `docs/plans/` — check them before making architectural changes
