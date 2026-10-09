# Regression testing

## Primary gate — automated E2E suite (`Fleans.E2E.Tests`)

The bulk of the BPMN regression catalog is automated under `src/Fleans/Fleans.E2E.Tests/`, driven by Playwright .NET + `Aspire.Hosting.Testing` against an in-process Aspire stack.

CI runs the suite on every PR (job `e2e` in `.github/workflows/dotnet.yml`); locally:

```bash
cd src/Fleans
dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E"
```

### Provider legs (`E2E-Smoke`)

The `e2e` job runs on the dev defaults (Sqlite persistence, Redis streaming). The `e2e-providers` matrix job re-runs the specs tagged `[TestCategory(E2ECategories.Smoke)]` — basic flow, user task, call activity, compensation, timer, message, signal — once per non-default provider: Postgres persistence, Kafka streaming, Azure Queue streaming (Azurite). `AspireFixture` passes `FLEANS_PERSISTENCE_PROVIDER` / `FLEANS_STREAMING_PROVIDER` through to the AppHost when set (defaulting persistence to Sqlite otherwise) and fails fast if the requested provider's resource isn't provisioned. Locally:

```bash
FLEANS_PERSISTENCE_PROVIDER=Postgres dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E-Smoke"
FLEANS_STREAMING_PROVIDER=Kafka      dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E-Smoke"
FLEANS_STREAMING_PROVIDER=AzureQueue dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E-Smoke"
```

Tag a spec `E2E-Smoke` only when it exercises persistence or streaming in a way the default leg can't vouch for — every tagged spec runs three extra times in CI.

### Split-role leg (`E2E-SplitRoles`)

Specs tagged `[TestCategory(E2ECategories.SplitRoles)]` (`SplitRoleTopologyTests`) need the three-role topology and run only in the `e2e-split-roles` job; the default `e2e` job filters them out (`TestCategory=E2E&TestCategory!=E2E-SplitRoles`). With `FLEANS_SPLIT_ROLES=true`:

- the AppHost runs `fleans-core` as `Core` and registers `fleans-worker` (`Fleans.WorkerHost`, `Worker`) in dev mode;
- `AspireFixture` sets `FLEANS_PLUGIN_HOST_PROJECT` to `src/Fleans/Fleans.E2E.PluginHost` — a test-only `Plugin`-role host (template-shaped: `Fleans.Worker` + Orleans only) whose single plugin `e2e-probe` reports the silo it ran on and its start/complete/cancel lifecycle to the in-process `TestHttpServer` (`/probe`);
- two explicit-start resources (`misroled-worker`, `misroled-plugin-host`) exist only for the role-mismatch spec;
- every resource's console output is teed to `Fleans.E2E.Tests/bin/Debug/net10.0/resource-logs/` (uploaded by CI on failure).

```bash
FLEANS_SPLIT_ROLES=true dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E-SplitRoles"
```

Without the switch these specs report Inconclusive. The plugin host only wires Redis streaming, so the leg is not combined with the provider matrix.

Each spec class under `Fleans.E2E.Tests/Specs/` carries a `// Ports tests/manual/NN-*/test-plan.md` doc comment linking back to the human-readable plan it derives from.

The `Specs/_DeferredManualPlans.cs` file documents every plan that doesn't yet have an active spec (editor-UI plans, custom-task plugin plans, OIDC/JWT auth plans, Helm/release-pipeline plans, etc.), each `[Ignore]`'d with a specific reason.

## Aspire.Hosting.Testing + `UseHttpsRedirection` trap

Aspire's default endpoint for an ASP.NET Core project is HTTPS, signed with an ASP.NET dev cert that **isn't trusted on Linux CI runners** (`HttpRequestException: The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot`).

Even requesting the `"http"` endpoint via `GetEndpoint(resource, "http")` / `CreateHttpClient(resource, "http")` doesn't help because `Fleans.Api` and `Fleans.Web` both call `app.UseHttpsRedirection()` in `Program.cs` — the HTTP request returns a 307 to the HTTPS endpoint, the HttpClient's auto-redirect handler follows it, and cert validation still fails.

**Fix** in `Fleans.E2E.Tests/Infrastructure/AspireFixture.cs`: build `HttpClient` manually with `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator`; the redirected HTTPS traffic is in-process test-cluster only so bypassing validation is safe. Playwright contexts need `IgnoreHTTPSErrors = true` for the same reason.

**Don't drop `UseHttpsRedirection()` from production code** to work around test setup — it's load-bearing for deployed services.

## `Microsoft.Playwright.MSTest` breaks MSTest 4 test discovery

Inheriting from `Microsoft.Playwright.MSTest.PageTest` (as the official Playwright .NET samples suggest) causes MSTest 4's discoverer to silently emit zero test cases for the subclass — even with `[TestClass]` + `[TestMethod]` on the derived class. The package was last updated for MSTest 3 semantics.

The workaround in `Fleans.E2E.Tests` is to drop `Microsoft.Playwright.MSTest` entirely and manage `IPlaywright` + `IBrowser` lifecycle manually from `AssemblyInitialize`, creating a fresh `IBrowserContext` + `IPage` per test in `[TestInitialize]`.

**Don't reintroduce the MSTest package** without first running `dotnet test --list-tests` and confirming the spec classes are visible.

## Fallback gate — manual catalog

The canonical manual catalog still lives in [`tests/manual/README.md`](../../tests/manual/README.md). Run a plan's `test-plan.md` manually when:

- (a) the corresponding automated spec is `[Ignore]`'d with a pending-investigation reason,
- (b) the plan is out-of-scope for automation (Docker-compose-only flows, Helm chart tests, release pipeline), or
- (c) you want to spot-check the UI-driven path (most automated specs drive Deploy + Start via API for speed; the manual plan exercises the bpmn-js drag-drop import + Fluent UI Start button).

## Prerequisites

### Automated suite

- Docker running (Aspire boots a Redis container).
- `pwsh` for the Playwright browser install: `pwsh src/Fleans/Fleans.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`, or via the `Microsoft.Playwright.dll` direct-invocation pattern on macOS without pwsh. See `.github/workflows/dotnet.yml` for the canonical CI invocation.

### Manual fallback (BPMN suite)

- Aspire stack running via `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- A clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`).
- Web UI reachable at `https://localhost:7124`; API origin `https://localhost:7140`.

### Website suite (always manual)

- `cd website && npm install` has been run at least once.
- `npx playwright install chromium` has been run at least once (only needed for plans that shell out to Playwright).
- Ports `4321` / `4327` / `4328` free.

## How to run a release-gating regression sweep

1. **CI** — confirm the `e2e` job on the PR branch is ✅ before promoting.
2. **Manual residual** — for any plan whose spec is `[Ignore]`'d in `_DeferredManualPlans` or carries a per-test `[Ignore]`, open the linked `test-plan.md` and execute the checklist end-to-end. Record results as `PASSED`, `FAILED`, `BUG` (new regression — file an issue), or `KNOWN BUG` (matches a `> **KNOWN BUG:** …` note inside the linked plan; counts as PASSED for promotion purposes).
3. **Website suite** — always manual; see `tests/manual/README.md#website-regression-suite`.
4. Aggregate results into the standard "Manual Regression Test Results" PR-issue comment, then promote (Review by Human) or reject (Ready) per the manual-regression-testing skill's normal flow.
